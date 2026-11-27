using System.Net;
using System.Text.Json;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>
/// Server or transport failure, carrying the protocol error code when the receiver answered with the
/// normative error envelope (PROTOCOL §0).
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode statusCode, string? errorCode, string message, JsonElement? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Details = details;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Value of <c>error.code</c>, e.g. <c>chunk_hash_mismatch</c>.</summary>
    public string? ErrorCode { get; }

    public JsonElement? Details { get; }
}
