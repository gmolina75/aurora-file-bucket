using AuroraFileBucket.Client.Cli;
using AuroraFileBucket.Client.Configuration;
using AuroraFileBucket.Client.Protocol;
using AuroraFileBucket.Client.Transfer;

namespace AuroraFileBucket.Client.Commands;

/// <summary>Resolves the effective settings of a transfer from the command line and the optional defaults.</summary>
public static class SettingsResolver
{
    public static UploadSettings Resolve(CliOptions options)
    {
        SenderDefaults defaults = SenderDefaults.Load();

        int chunkSizeMiB = options.ChunkSizeMiB ?? defaults.ChunkSizeMiB;
        int concurrency = options.Concurrency ?? defaults.MaxConcurrency;
        int bandwidth = options.MaxBandwidthMbps ?? defaults.MaxBandwidthMbps;
        int maxRetries = options.MaxRetries ?? 5;
        string stateDb = options.StateDbPath ?? defaults.StateDbPath;

        if (chunkSizeMiB < ChunkPlan.MinChunkSizeMiB || chunkSizeMiB > ChunkPlan.MaxChunkSizeMiB)
        {
            throw new UsageException(
                $"--chunk-size-mib must be between {ChunkPlan.MinChunkSizeMiB} and {ChunkPlan.MaxChunkSizeMiB} (got {chunkSizeMiB}).");
        }

        if (concurrency < 1 || concurrency > 6)
        {
            throw new UsageException($"--concurrency must be between 1 and 6 (got {concurrency}).");
        }

        if (bandwidth < 1 || bandwidth > 1000)
        {
            throw new UsageException($"--max-bandwidth-mbps must be between 1 and 1000 (got {bandwidth}).");
        }

        return new UploadSettings
        {
            ServerUrl = options.ServerUrl ?? throw new UsageException("--server is required."),
            FilePath = options.FilePath ?? throw new UsageException("--file is required."),
            VaultSlug = options.VaultSlug,
            Email = options.Email,
            Password = options.Password,
            AccessToken = options.AccessToken,
            ChunkSizeMiB = chunkSizeMiB,
            Concurrency = concurrency,
            MaxBandwidthMbps = bandwidth,
            NoCompress = options.NoCompress,
            StateDbPath = stateDb,
            MaxRetries = maxRetries,
            NoResume = options.NoResume,
            JsonProgress = options.JsonProgress,
            Adaptive = options.Adaptive,
            CompressibilityThreshold = defaults.CompressibilityThreshold,
        };
    }
}

/// <summary>Runs the upload command.</summary>
public sealed class UploadCommand
{
    private readonly CliOptions _options;

    public UploadCommand(CliOptions options)
    {
        _options = options;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        UploadSettings settings = SettingsResolver.Resolve(_options);
        var service = new UploadService(settings);
        UploadResult result = await service.RunAsync(cancellationToken).ConfigureAwait(false);

        if (!_options.JsonProgress)
        {
            Console.WriteLine($"Upload '{result.UploadId}' completed and verified by the receiver.");
        }

        return ExitCodes.Success;
    }
}

/// <summary>Runs the <c>status</c> sub command, which queries the state of an existing session.</summary>
public sealed class StatusCommand
{
    private readonly CliOptions _options;

    public StatusCommand(CliOptions options)
    {
        _options = options;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string serverUrl = _options.ServerUrl ?? throw new UsageException("--server is required.");
        string uploadId = _options.UploadId ?? throw new UsageException("--upload-id is required.");
        string baseUrl = AuroraApiClient.NormalizeBaseUrl(serverUrl);

        using var http = HttpClientFactory.Create(1);
        var delay = SystemDelayProvider.Instance;
        var auth = new AuthService(http, baseUrl, delay);
        await auth.AuthenticateAsync(_options.Email, _options.Password, _options.AccessToken, cancellationToken).ConfigureAwait(false);

        var api = new AuroraApiClient(http, auth, delay, _options.MaxRetries ?? 5, Guid.NewGuid().ToString("D"), baseUrl);
        UploadSession status = await api.GetStatusAsync(uploadId, cancellationToken).ConfigureAwait(false);

        if (_options.JsonProgress)
        {
            Console.WriteLine(ApiJson.Serialize(status));
        }
        else
        {
            Console.WriteLine($"uploadId : {status.UploadId}");
            Console.WriteLine($"status   : {status.Status}");
            Console.WriteLine($"vault    : {status.VaultSlug}");
            Console.WriteLine($"file     : {status.FileName} ({status.TotalBytes} bytes)");
            Console.WriteLine($"parts    : {status.ReceivedParts.Count}/{status.TotalParts} received");
            Console.WriteLine($"bytes    : {status.BytesReceived}/{status.TotalBytes}");
            if (!string.IsNullOrEmpty(status.VaultPath))
            {
                Console.WriteLine($"vaultPath: {status.VaultPath}");
            }

            if (!string.IsNullOrEmpty(status.FinalSha256))
            {
                Console.WriteLine($"sha256   : {status.FinalSha256}");
            }

            if (status.Error is { } error)
            {
                Console.WriteLine($"error    : {error.Code} {error.Message}");
            }
        }

        return ExitCodes.Success;
    }
}
