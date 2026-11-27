using AuroraFileBucket.Server.Contracts;
using AuroraFileBucket.Server.Infrastructure;
using AuroraFileBucket.Server.Services;

namespace AuroraFileBucket.Server.Endpoints;

/// <summary>The token lifecycle: <c>POST /v1/auth/login</c> and <c>POST /v1/auth/refresh</c>.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/auth").WithTags("auth");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithName("Login");

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithName("Refresh");
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest? request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var outcome = await auth.LoginAsync(request, cancellationToken);
        if (!outcome.Succeeded)
        {
            return ApiError.Result(outcome.StatusCode, outcome.ErrorCode!, outcome.ErrorMessage!);
        }

        return Results.Json(outcome.Tokens, contentType: "application/json; charset=utf-8");
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest? request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var outcome = await auth.RefreshAsync(request, cancellationToken);
        if (!outcome.Succeeded)
        {
            return ApiError.Result(outcome.StatusCode, outcome.ErrorCode!, outcome.ErrorMessage!);
        }

        return Results.Json(outcome.Tokens, contentType: "application/json; charset=utf-8");
    }
}
