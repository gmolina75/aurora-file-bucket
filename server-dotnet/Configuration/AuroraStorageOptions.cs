namespace AuroraFileBucket.Server.Configuration;

/// <summary>
/// Storage layout configuration bound from the <c>AuroraStorage</c> section.
/// Environment overrides use the standard double underscore separator, e.g.
/// <c>AuroraStorage__StagingRoot</c> or <c>AuroraStorage__Vaults__0__QuotaGb</c>.
/// </summary>
public sealed class AuroraStorageOptions
{
    public const string SectionName = "AuroraStorage";

    /// <summary>Root folder that holds one directory per upload session.</summary>
    public string StagingRoot { get; set; } = string.Empty;

    /// <summary>Root folder that holds the isolated vault directories.</summary>
    public string VaultsRoot { get; set; } = string.Empty;

    /// <summary>SQLite file. Relative paths resolve inside <c>{contentRoot}/data</c>.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    public int DefaultChunkSizeMiB { get; set; } = 16;

    public int MinChunkSizeMiB { get; set; } = 4;

    public int MaxChunkSizeMiB { get; set; } = 32;

    /// <summary>Sessions without activity for this many hours are purged and marked EXPIRED.</summary>
    public int OrphanSessionHours { get; set; } = 72;

    /// <summary>Delete the staging folder right after a successful assembly.</summary>
    public bool ImmediatePostAssemblyCleanup { get; set; } = true;

    /// <summary>Disk usage percentage above which the receiver answers 429.</summary>
    public int BackpressureDiskUsagePercent { get; set; } = 85;

    public List<VaultOptions> Vaults { get; set; } = new();
}

/// <summary>One isolated vault (slug, quota and NTFS path).</summary>
public sealed class VaultOptions
{
    public string Slug { get; set; } = string.Empty;

    public double QuotaGb { get; set; } = 500;

    /// <summary>Absolute NTFS path. Empty means <c>{VaultsRoot}/{slug}</c>.</summary>
    public string Path { get; set; } = string.Empty;
}
