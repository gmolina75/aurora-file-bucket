using System.Text.Json;
using System.Text.Json.Serialization;
using AuroraFileBucket.Server.Contracts;

namespace AuroraFileBucket.Server.Infrastructure;

/// <summary>
/// Helpers that write the normative error envelope
/// <c>{"error":{"code","message","details"?}}</c> for every failure path.
/// </summary>
public static class ApiError
{
    /// <summary>Protocol error codes used by the receiver.</summary>
    public static class Codes
    {
        public const string InvalidRequest = "invalid_request";
        public const string InvalidManifest = "invalid_manifest";
        public const string InvalidCredentials = "invalid_credentials";
        public const string InvalidRefreshToken = "invalid_refresh_token";
        public const string EmailNotAllowed = "email_not_allowed";
        public const string Unauthorized = "unauthorized";
        public const string Forbidden = "forbidden";
        public const string NotFound = "not_found";
        public const string VaultNotAllowed = "vault_not_allowed";
        public const string PartConflict = "part_conflict";
        public const string ChunkHashMismatch = "chunk_hash_mismatch";
        public const string IncompleteUpload = "incomplete_upload";
        public const string InvalidState = "invalid_state";
        public const string QuotaExceeded = "quota_exceeded";
        public const string Backpressure = "backpressure";
        public const string GlobalHashMismatch = "global_hash_mismatch";
        public const string InternalError = "internal_error";
    }

    public static IResult Result(
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorBody(code, message, details)),
            statusCode: statusCode,
            contentType: "application/json; charset=utf-8");
    }

    public static IResult BadRequest(string code, string message, IReadOnlyDictionary<string, object?>? details = null)
        => Result(StatusCodes.Status400BadRequest, code, message, details);

    public static IResult Unauthorized(string code, string message)
        => Result(StatusCodes.Status401Unauthorized, code, message);

    public static IResult Forbidden(string code, string message)
        => Result(StatusCodes.Status403Forbidden, code, message);

    public static IResult NotFound(string message = "Upload session not found.")
        => Result(StatusCodes.Status404NotFound, Codes.NotFound, message);

    public static IResult Conflict(string code, string message, IReadOnlyDictionary<string, object?>? details = null)
        => Result(StatusCodes.Status409Conflict, code, message, details);

    public static IResult PayloadTooLarge(string code, string message, IReadOnlyDictionary<string, object?>? details = null)
        => Result(StatusCodes.Status413PayloadTooLarge, code, message, details);

    /// <summary>429 with the mandatory <c>Retry-After</c> header (seconds).</summary>
    public static IResult Backpressure(HttpContext context, int retryAfterSeconds, string message)
    {
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Result(StatusCodes.Status429TooManyRequests, Codes.Backpressure, message);
    }

    /// <summary>Writes the envelope straight to the response (used by auth challenge handlers).</summary>
    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(new ErrorEnvelope(new ErrorBody(code, message)), options));
    }
}

/// <summary>
/// Converts framework generated problem details (malformed JSON, unsupported media type, ...)
/// into the protocol error envelope so every error response has the same shape.
/// </summary>
public sealed class AuroraProblemDetailsService : IProblemDetailsService
{
    public ValueTask TryWriteAsync(ProblemDetailsContext problemDetailsContext)
    {
        if (problemDetailsContext.HttpContext.Response.HasStarted)
        {
            return ValueTask.CompletedTask;
        }

        return WriteAsync(problemDetailsContext);
    }

    public async ValueTask WriteAsync(ProblemDetailsContext problemDetailsContext)
    {
        var httpContext = problemDetailsContext.HttpContext;
        var problem = problemDetailsContext.ProblemDetails;
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;

        var (code, message) = status switch
        {
            StatusCodes.Status400BadRequest => (ApiError.Codes.InvalidRequest, "The request could not be parsed."),
            StatusCodes.Status401Unauthorized => (ApiError.Codes.Unauthorized, "Authentication is required."),
            StatusCodes.Status403Forbidden => (ApiError.Codes.Forbidden, "Access to this resource is forbidden."),
            StatusCodes.Status404NotFound => (ApiError.Codes.NotFound, "Resource not found."),
            StatusCodes.Status405MethodNotAllowed => (ApiError.Codes.InvalidRequest, "Method not allowed."),
            StatusCodes.Status409Conflict => (ApiError.Codes.InvalidState, "Conflicting state."),
            StatusCodes.Status413PayloadTooLarge => (ApiError.Codes.QuotaExceeded, "Payload too large."),
            StatusCodes.Status415UnsupportedMediaType => (ApiError.Codes.InvalidRequest, "Unsupported media type."),
            StatusCodes.Status429TooManyRequests => (ApiError.Codes.Backpressure, "Server is busy, retry later."),
            _ => (ApiError.Codes.InternalError, "Unexpected server error.")
        };

        if (status >= 500)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(
                new ErrorEnvelope(new ErrorBody(code, message)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }

        await ApiError.WriteAsync(httpContext, status, code, problem.Title ?? message);
    }
}
