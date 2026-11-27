namespace AuroraFileBucket.Client.Cli;

/// <summary>Sub command selected on the command line.</summary>
public enum CommandKind
{
    Upload,
    Status,
    Help,
}

/// <summary>Parsed command line. Flags always win over the optional <c>appsettings.json</c> defaults.</summary>
public sealed class CliOptions
{
    public CommandKind Command { get; init; } = CommandKind.Upload;

    public string? ServerUrl { get; init; }

    public string? FilePath { get; init; }

    public string? VaultSlug { get; init; }

    public string? Email { get; init; }

    public string? Password { get; init; }

    public string? AccessToken { get; init; }

    public int? ChunkSizeMiB { get; init; }

    public int? Concurrency { get; init; }

    public int? MaxBandwidthMbps { get; init; }

    public bool NoCompress { get; init; }

    public string? StateDbPath { get; init; }

    public int? MaxRetries { get; init; }

    public bool NoResume { get; init; }

    public bool JsonProgress { get; init; }

    public bool Adaptive { get; init; }

    public string? UploadId { get; init; }
}
