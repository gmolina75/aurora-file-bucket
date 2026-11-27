using AuroraFileBucket.Server.Configuration;

namespace AuroraFileBucket.Server.Services;

/// <summary>One resolved vault: slug, quota in bytes and absolute NTFS path.</summary>
public sealed record VaultDefinition(string Slug, double QuotaGb, string Path)
{
    public long QuotaBytes => (long)(QuotaGb * 1024d * 1024d * 1024d);
}

/// <summary>
/// Effective on-disk layout. Resolved once at startup from configuration so every request
/// and worker uses exactly the same paths.
/// </summary>
public sealed class StorageLayout
{
    private StorageLayout(
        string stagingRoot,
        string vaultsRoot,
        string databasePath,
        IReadOnlyList<VaultDefinition> vaults,
        bool usesFallback)
    {
        StagingRoot = stagingRoot;
        VaultsRoot = vaultsRoot;
        DatabasePath = databasePath;
        Vaults = vaults;
        UsesFallbackPaths = usesFallback;
    }

    /// <summary>Folder that holds one directory per upload session.</summary>
    public string StagingRoot { get; }

    /// <summary>Folder that holds the vault directories.</summary>
    public string VaultsRoot { get; }

    /// <summary>SQLite database file.</summary>
    public string DatabasePath { get; }

    public IReadOnlyList<VaultDefinition> Vaults { get; }

    /// <summary>True when any path had to fall back to the local data directory.</summary>
    public bool UsesFallbackPaths { get; }

    public VaultDefinition? FindVault(string slug)
        => Vaults.FirstOrDefault(v => string.Equals(v.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public string StagingFolderFor(string uploadId) => Path.Combine(StagingRoot, uploadId);

    public string StagingPartPath(string uploadId, int partNumber)
        => Path.Combine(StagingRoot, uploadId, $"part_{partNumber:D5}.bin");

    /// <summary>
    /// Resolves the effective layout. Missing or unusable configuration falls back to
    /// <c>{contentRoot}/data</c> and logs a warning; the receiver always starts.
    /// </summary>
    public static StorageLayout Resolve(AuroraStorageOptions options, string contentRoot, ILogger logger)
    {
        var dataRoot = Path.Combine(contentRoot, "data");
        var usesFallback = false;

        var stagingRoot = ResolveDirectory(options.StagingRoot, contentRoot, dataRoot, "staging", "StagingRoot", logger, ref usesFallback);
        var vaultsRoot = ResolveDirectory(options.VaultsRoot, contentRoot, dataRoot, "vaults", "VaultsRoot", logger, ref usesFallback);

        var databasePath = ResolveDatabasePath(options.DatabasePath, contentRoot, dataRoot, logger, ref usesFallback);

        var vaults = new List<VaultDefinition>();
        foreach (var vault in options.Vaults ?? new List<VaultOptions>())
        {
            if (string.IsNullOrWhiteSpace(vault.Slug))
            {
                logger.LogWarning("Ignoring a vault without slug.");
                continue;
            }

            var path = string.IsNullOrWhiteSpace(vault.Path)
                ? Path.Combine(vaultsRoot, vault.Slug)
                : Path.IsPathRooted(vault.Path)
                    ? vault.Path
                    : Path.Combine(contentRoot, vault.Path);

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Vault {Slug} path {Path} is not usable; the vault is disabled.", vault.Slug, path);
                continue;
            }

            vaults.Add(new VaultDefinition(vault.Slug, vault.QuotaGb <= 0 ? 500 : vault.QuotaGb, path));
        }

        if (vaults.Count == 0)
        {
            var defaultPath = Path.Combine(vaultsRoot, "default");
            Directory.CreateDirectory(defaultPath);
            logger.LogWarning(
                "No vault configured. Falling back to the default vault 'default' (quota 500 GB) at {Path}.",
                defaultPath);
            vaults.Add(new VaultDefinition("default", 500, defaultPath));
            usesFallback = true;
        }

        return new StorageLayout(stagingRoot, vaultsRoot, databasePath, vaults, usesFallback);
    }

    private static string ResolveDirectory(
        string configured,
        string contentRoot,
        string dataRoot,
        string fallbackFolder,
        string settingName,
        ILogger logger,
        ref bool usesFallback)
    {
        var fallback = Path.Combine(dataRoot, fallbackFolder);

        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning(
                "AuroraStorage:{Setting} is not configured. Falling back to the local data directory {Path}.",
                settingName,
                fallback);
            Directory.CreateDirectory(fallback);
            usesFallback = true;
            return fallback;
        }

        var resolved = Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);

        try
        {
            Directory.CreateDirectory(resolved);
            return resolved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex,
                "AuroraStorage:{Setting} ({Path}) is not usable. Falling back to the local data directory {Fallback}.",
                settingName,
                resolved,
                fallback);
            Directory.CreateDirectory(fallback);
            usesFallback = true;
            return fallback;
        }
    }

    private static string ResolveDatabasePath(
        string configured,
        string contentRoot,
        string dataRoot,
        ILogger logger,
        ref bool usesFallback)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning(
                "AuroraStorage:DatabasePath is not configured. Falling back to {Path}.",
                Path.Combine(dataRoot, "aurora.db"));
            usesFallback = true;
            return Path.Combine(dataRoot, "aurora.db");
        }

        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        // Relative database paths live inside the application data directory so the
        // database never lands in the source tree.
        return Path.Combine(dataRoot, configured);
    }
}
