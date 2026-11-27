namespace AuroraFileBucket.Server.Configuration;

/// <summary>
/// Authentication configuration bound from the <c>Auth</c> section.
/// Environment overrides: <c>Auth__JwtSigningKey</c>, <c>Auth__Users__0__Email</c>, ...
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Access token lifetime in minutes (protocol range 15-30).</summary>
    public int AccessTokenMinutes { get; set; } = 20;

    /// <summary>Refresh token lifetime in hours.</summary>
    public int RefreshTokenHours { get; set; } = 24;

    /// <summary>HMAC-SHA256 signing key. Empty means an ephemeral key is generated at startup.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    public string JwtIssuer { get; set; } = "aurora-file-bucket";

    public string JwtAudience { get; set; } = "aurora-file-bucket";

    /// <summary>When not empty, only these e-mails may authenticate and only they are admins.</summary>
    public List<string> AllowedAdminEmails { get; set; } = new();

    public List<UserOptions> Users { get; set; } = new();
}

/// <summary>Development-mode user. An empty password disables the account.</summary>
public sealed class UserOptions
{
    public string Email { get; set; } = string.Empty;

    /// <summary>Plain text password (development mode only). Empty disables the account.</summary>
    public string Password { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Vault slugs the issued token is allowed to write to.</summary>
    public List<string> Vaults { get; set; } = new();
}
