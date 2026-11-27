using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AuroraFileBucket.Server.Tests;

/// <summary>
/// Hosts the real receiver in memory. Every instance owns a private temporary folder,
/// so the tests never touch D:\Staging, the real vaults or a production database.
/// </summary>
public sealed class AuroraAppFactory : WebApplicationFactory<Program>
{
    public AuroraAppFactory()
    {
        Root = Path.Combine(Path.GetTempPath(), "aurora-file-bucket-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Private temporary root of this test instance.</summary>
    public string Root { get; }

    public string StagingRoot => Path.Combine(Root, "staging");

    public string VaultsRoot => Path.Combine(Root, "vaults");

    public string DatabasePath => Path.Combine(Root, "aurora.db");

    public string VaultPath(string slug) => Path.Combine(VaultsRoot, slug);

    public const string DevEmail = "dev@example.com";
    public const string DevPassword = "dev-password";

    /// <summary>Second user, only allowed to write to the 'other' vault.</summary>
    public const string OtherEmail = "other@example.com";
    public const string OtherPassword = "other-password";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuroraStorage:StagingRoot"] = StagingRoot,
                ["AuroraStorage:VaultsRoot"] = VaultsRoot,
                ["AuroraStorage:DatabasePath"] = DatabasePath,
                ["AuroraStorage:MinChunkSizeMiB"] = "4",
                ["AuroraStorage:MaxChunkSizeMiB"] = "32",
                ["Auth:JwtSigningKey"] = "test-signing-key-that-is-at-least-32-bytes-long-for-hs256",
                ["Auth:Users:0:Email"] = DevEmail,
                ["Auth:Users:0:Password"] = DevPassword,
                ["Auth:Users:0:DisplayName"] = "Dev User",
                ["Auth:Users:0:Vaults:0"] = "default",
                ["Auth:Users:1:Email"] = OtherEmail,
                ["Auth:Users:1:Password"] = OtherPassword,
                ["Auth:Users:1:DisplayName"] = "Other User",
                ["Auth:Users:1:Vaults:0"] = "other"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // A leftover temporary folder is harmless.
        }
    }
}
