namespace AuroraFileBucket.Server.Services;

/// <summary>
/// Vault quota accounting. The sum of the files already stored in the vault plus the
/// size of the incoming transfer must fit in <c>AuroraStorage:Vaults[].quotaGb</c>.
/// </summary>
public sealed class VaultQuotaService
{
    private readonly ILogger<VaultQuotaService> _logger;

    public VaultQuotaService(ILogger<VaultQuotaService> logger) => _logger = logger;

    /// <summary>Bytes currently stored inside the vault.</summary>
    public long GetUsageBytes(VaultDefinition vault)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(vault.Path))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(vault.Path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Skipped {File} while measuring the vault usage.", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not measure the usage of vault {Slug}.", vault.Slug);
        }

        return total;
    }

    /// <summary>True when storing <paramref name="incomingBytes"/> more would break the quota.</summary>
    public bool WouldExceedQuota(VaultDefinition vault, long incomingBytes, out long usageBytes)
    {
        usageBytes = GetUsageBytes(vault);
        return usageBytes + incomingBytes > vault.QuotaBytes;
    }
}
