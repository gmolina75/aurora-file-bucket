using System.Security.Claims;

namespace AuroraFileBucket.Server.Infrastructure;

/// <summary>Helpers to read the claims of the access token.</summary>
public static class PrincipalExtensions
{
    /// <summary>The <c>sub</c> claim: the e-mail of the authenticated user.</summary>
    public static string GetEmail(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
    }

    /// <summary>The <c>vaults</c> claim: the vault slugs the token may write to.</summary>
    public static IReadOnlyList<string> GetVaults(this ClaimsPrincipal principal)
    {
        return principal.FindAll("vaults")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    /// <summary>True when the token is allowed to write to the given vault slug.</summary>
    public static bool IsVaultAllowed(this ClaimsPrincipal principal, string slug)
    {
        return principal.GetVaults()
            .Any(allowed => string.Equals(allowed, slug, StringComparison.OrdinalIgnoreCase));
    }
}
