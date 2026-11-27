using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuroraFileBucket.Server.Configuration;
using AuroraFileBucket.Server.Contracts;
using AuroraFileBucket.Server.Data;
using AuroraFileBucket.Server.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuroraFileBucket.Server.Services;

/// <summary>Outcome of a login or refresh attempt.</summary>
public sealed record AuthOutcome(bool Succeeded, TokenPair? Tokens, int StatusCode, string? ErrorCode, string? ErrorMessage)
{
    public static AuthOutcome Ok(TokenPair tokens) => new(true, tokens, StatusCodes.Status200OK, null, null);

    public static AuthOutcome Fail(int statusCode, string code, string message)
        => new(false, null, statusCode, code, message);
}

/// <summary>
/// Issues the token pair of the protocol: a short lived HS256 JWT plus an opaque
/// single use refresh token that is only persisted as a SHA-256 hash.
/// </summary>
public sealed class AuthService
{
    private readonly AuthOptions _options;
    private readonly SigningKey _signingKey;
    private readonly RefreshTokenRepository _refreshTokens;
    private readonly TimeProvider _clock;

    public AuthService(
        IOptions<AuthOptions> options,
        SigningKey signingKey,
        RefreshTokenRepository refreshTokens,
        TimeProvider clock)
    {
        _options = options.Value;
        _signingKey = signingKey;
        _refreshTokens = refreshTokens;
        _clock = clock;
    }

    public async Task<AuthOutcome> LoginAsync(LoginRequest? request, CancellationToken cancellationToken)
    {
        var email = request?.Email?.Trim() ?? string.Empty;
        var password = request?.Password ?? string.Empty;

        if (email.Length == 0 || password.Length == 0)
        {
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidCredentials, "Invalid credentials.");
        }

        if (_options.AllowedAdminEmails is { Count: > 0 }
            && !_options.AllowedAdminEmails.Any(allowed => string.Equals(allowed?.Trim(), email, StringComparison.OrdinalIgnoreCase)))
        {
            return AuthOutcome.Fail(StatusCodes.Status403Forbidden, ApiError.Codes.EmailNotAllowed, "This e-mail address is not allowed to use the receiver.");
        }

        var user = FindUser(email);
        if (user is null)
        {
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidCredentials, "Invalid credentials.");
        }

        // An empty password marks the account as disabled.
        if (string.IsNullOrEmpty(user.Password))
        {
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidCredentials, "Invalid credentials.");
        }

        if (!FixedTimeEquals(user.Password, password))
        {
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidCredentials, "Invalid credentials.");
        }

        var pair = await IssueAsync(user, cancellationToken);
        return AuthOutcome.Ok(pair);
    }

    public async Task<AuthOutcome> RefreshAsync(RefreshRequest? request, CancellationToken cancellationToken)
    {
        var presented = request?.RefreshToken ?? string.Empty;
        if (presented.Length == 0)
        {
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidRefreshToken, "The refresh token is invalid.");
        }

        var hash = HashToken(presented);
        var row = await _refreshTokens.GetAsync(hash, cancellationToken);

        if (row is null)
        {
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidRefreshToken, "The refresh token is invalid.");
        }

        var now = _clock.GetUtcNow();

        // Reuse of an already revoked token revokes every refresh token of that user.
        if (row.RevokedAt is not null)
        {
            await _refreshTokens.RevokeAllForUserAsync(row.UserEmail, ProtocolTime.ToProtocol(now), cancellationToken);
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidRefreshToken, "The refresh token is invalid.");
        }

        if (DateTimeOffset.TryParse(row.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)
            && expires <= now)
        {
            await _refreshTokens.RevokeAsync(hash, ProtocolTime.ToProtocol(now), null, cancellationToken);
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidRefreshToken, "The refresh token is invalid.");
        }

        var user = FindUser(row.UserEmail);
        if (user is null)
        {
            await _refreshTokens.RevokeAllForUserAsync(row.UserEmail, ProtocolTime.ToProtocol(now), cancellationToken);
            return AuthOutcome.Fail(StatusCodes.Status401Unauthorized, ApiError.Codes.InvalidRefreshToken, "The refresh token is invalid.");
        }

        var pair = await IssueAsync(user, cancellationToken, previousHash: hash);
        return AuthOutcome.Ok(pair);
    }

    /// <summary>Builds the access token claims: sub, name, vaults, iat, exp and jti.</summary>
    public string CreateAccessToken(UserOptions user, DateTimeOffset now, out DateTimeOffset expiresAt)
    {
        expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Email),
            new("name", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new(
                JwtRegisteredClaimNames.Iat,
                EpochTime.GetIntDate(now.UtcDateTime).ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64)
        };

        foreach (var vault in user.Vaults ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(vault))
            {
                claims.Add(new Claim("vaults", vault));
            }
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(_signingKey.Key),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.JwtIssuer,
            audience: _options.JwtAudience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task<TokenPair> IssueAsync(
        UserOptions user,
        CancellationToken cancellationToken,
        string? previousHash = null)
    {
        var now = _clock.GetUtcNow();
        var accessToken = CreateAccessToken(user, now, out var expiresAt);

        var refreshToken = CreateOpaqueToken();
        var refreshHash = HashToken(refreshToken);
        var refreshExpires = now.AddHours(_options.RefreshTokenHours);

        await _refreshTokens.InsertAsync(refreshHash, user.Email, ProtocolTime.ToProtocol(refreshExpires), cancellationToken);

        if (previousHash is not null)
        {
            await _refreshTokens.RevokeAsync(previousHash, ProtocolTime.ToProtocol(now), refreshHash, cancellationToken);
        }

        var vaults = (user.Vaults ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        return new TokenPair(
            TokenType: "Bearer",
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresIn: (int)Math.Max(0, (expiresAt - now).TotalSeconds),
            User: new UserInfo(user.Email, string.IsNullOrWhiteSpace(user.DisplayName) ? null : user.DisplayName),
            Vaults: vaults);
    }

    private static string CreateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncoder.Encode(bytes);
    }

    private UserOptions? FindUser(string email)
        => _options.Users?.FirstOrDefault(u => string.Equals(u.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase));

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        if (a.Length != b.Length)
        {
            // Still burn a comparison so the timing does not leak the length.
            CryptographicOperations.FixedTimeEquals(a, a);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
