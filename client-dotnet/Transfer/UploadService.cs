using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using AuroraFileBucket.Client.Protocol;

namespace AuroraFileBucket.Client.Transfer;

/// <summary>Effective settings of a transfer, after merging the command line with the optional defaults.</summary>
public sealed class UploadSettings
{
    public required string ServerUrl { get; init; }

    public required string FilePath { get; init; }

    public string? VaultSlug { get; init; }

    public string? Email { get; init; }

    public string? Password { get; init; }

    public string? AccessToken { get; init; }

    public int ChunkSizeMiB { get; init; } = 16;

    public int Concurrency { get; init; } = 3;

    public int MaxBandwidthMbps { get; init; } = 50;

    public bool NoCompress { get; init; }

    public string StateDbPath { get; init; } = "aurora_state.db";

    public int MaxRetries { get; init; } = 5;

    public bool NoResume { get; init; }

    public bool JsonProgress { get; init; }

    public bool Adaptive { get; init; }

    public double CompressibilityThreshold { get; init; } = CompressionPolicy.DefaultCompressibilityThreshold;
}

/// <summary>Outcome of a transfer.</summary>
public sealed class UploadResult
{
    public string UploadId { get; init; } = string.Empty;

    public string? VaultSlug { get; init; }

    public string? VaultPath { get; init; }

    public string? FinalSha256 { get; init; }

    public long BytesSent { get; init; }

    public int PartsSent { get; init; }

    public int PartsSkipped { get; init; }

    public double DurationSeconds { get; init; }

    public bool CompressionEnabled { get; init; }

    public int ChunkSize { get; init; }

    public int TotalParts { get; init; }
}

/// <summary>One part ready to be uploaded. Owns the pooled buffer with the transferred bytes.</summary>
internal sealed class PreparedPart : IDisposable
{
    public int PartNumber { get; init; }

    public byte[] Body { get; init; } = Array.Empty<byte>();

    public int BodyLength { get; init; }

    public int RawLength { get; init; }

    /// <summary><c>zstd</c> or <c>none</c>.</summary>
    public string Compression { get; init; } = "none";

    public string ChunkSha256 { get; init; } = string.Empty;

    public string RawSha256 { get; init; } = string.Empty;

    public void Dispose() => System.Buffers.ArrayPool<byte>.Shared.Return(Body);
}

/// <summary>
/// Orchestrates the whole transfer: authentication, manifest, chunked upload with compression and
/// bandwidth control, final hash and status polling.
/// </summary>
public sealed class UploadService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromHours(6);

    private readonly UploadSettings _settings;
    private readonly HttpMessageHandler? _handler;
    private readonly IDelayProvider _delay;

    public UploadService(UploadSettings settings, HttpMessageHandler? handler = null, IDelayProvider? delay = null)
    {
        _settings = settings;
        _handler = handler;
        _delay = delay ?? Protocol.SystemDelayProvider.Instance;
    }

    public async Task<UploadResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var file = new FileInfo(_settings.FilePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"The file '{_settings.FilePath}' does not exist.", _settings.FilePath);
        }

        long totalBytes = file.Length;
        if (totalBytes <= 0)
        {
            throw new InvalidOperationException($"The file '{_settings.FilePath}' is empty.");
        }

        double fileMtime = (file.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
        int chunkSize = _settings.ChunkSizeMiB * 1024 * 1024;
        int totalParts = ChunkPlan.TotalParts(totalBytes, chunkSize);
        string serverKey = _settings.ServerUrl.Trim().TrimEnd('/');
        string vaultKey = _settings.VaultSlug ?? string.Empty;
        string baseUrl = Protocol.AuroraApiClient.NormalizeBaseUrl(_settings.ServerUrl);
        string clientId = Guid.NewGuid().ToString("D");

        using var http = Protocol.HttpClientFactory.Create(_settings.Concurrency, _handler);

        var auth = new Protocol.AuthService(http, baseUrl, _delay);
        await auth.AuthenticateAsync(_settings.Email, _settings.Password, _settings.AccessToken, cancellationToken).ConfigureAwait(false);

        var api = new Protocol.AuroraApiClient(http, auth, _delay, _settings.MaxRetries, clientId, baseUrl);
        var progress = new ProgressReporter(_settings.JsonProgress, totalBytes, totalParts);

        // Compressibility probe: compression is dropped for the whole session when the first 2 MB do
        // not compress enough (PROTOCOL §4.3).
        bool compressionEnabled = !_settings.NoCompress;
        if (compressionEnabled)
        {
            bool worthwhile = await CompressionCodec
                .ProbeAsync(_settings.FilePath, _settings.CompressibilityThreshold, cancellationToken)
                .ConfigureAwait(false);

            if (!worthwhile)
            {
                compressionEnabled = false;
                progress.WriteEvent("compression-disabled", new Dictionary<string, object?>
                {
                    ["reason"] = "compressibility-probe",
                    ["threshold"] = _settings.CompressibilityThreshold,
                });
            }
        }

        using var store = new ResumeStateStore(_settings.StateDbPath);

        var skip = new HashSet<int>();
        string uploadId = string.Empty;
        string? vaultSlug = _settings.VaultSlug;

        if (!_settings.NoResume)
        {
            var row = await store
                .FindProgressAsync(file.FullName, totalBytes, serverKey, vaultKey, cancellationToken)
                .ConfigureAwait(false);

            if (row is not null && Math.Abs(row.FileMtime - fileMtime) < 0.001)
            {
                // The receiver is the authority on the parts that exist (PROTOCOL §4.2 step 2).
                var status = await api.GetStatusAsync(row.UploadId, cancellationToken).ConfigureAwait(false);

                bool reusable = status.Status is not (UploadStates.Failed or "EXPIRED") &&
                               status.ChunkSize == chunkSize &&
                               status.TotalParts == totalParts;

                if (reusable)
                {
                    uploadId = status.UploadId;
                    vaultSlug = status.VaultSlug ?? vaultSlug;
                    skip = status.ReceivedParts.ToHashSet();
                    progress.WriteEvent("resumed", new Dictionary<string, object?>
                    {
                        ["uploadId"] = uploadId,
                        ["partsAlreadyReceived"] = skip.Count,
                    });
                }
            }
        }

        if (string.IsNullOrEmpty(uploadId))
        {
            var initRequest = new Protocol.InitRequest
            {
                FileName = file.Name,
                TotalBytes = totalBytes,
                ChunkSize = chunkSize,
                TotalParts = totalParts,
                FileSha256 = null,
                Compression = compressionEnabled ? "auto" : "none",
            };

            var session = await api.InitAsync(initRequest, _settings.VaultSlug, cancellationToken).ConfigureAwait(false);
            uploadId = session.UploadId;
            vaultSlug = session.VaultSlug ?? vaultSlug;
        }

        await store.SaveProgressAsync(
            new UploadProgressRow
            {
                FilePath = file.FullName,
                FileSize = totalBytes,
                FileMtime = fileMtime,
                ServerUrl = serverKey,
                VaultSlug = vaultKey,
                UploadId = uploadId,
                ChunkSize = chunkSize,
                TotalParts = totalParts,
                FileSha256 = null,
                State = UploadStates.Uploading,
                UpdatedAt = ResumeStateStore.Timestamp(DateTimeOffset.UtcNow),
            },
            cancellationToken).ConfigureAwait(false);

        var bucket = TokenBucket.FromMbps(_settings.MaxBandwidthMbps);
        var transfer = await TransferPartsAsync(
            api,
            store,
            progress,
            bucket,
            uploadId,
            _settings.FilePath,
            totalBytes,
            chunkSize,
            totalParts,
            compressionEnabled,
            skip,
            cancellationToken).ConfigureAwait(false);

        // The global digest is computed incrementally while reading the chunks, never in a second pass.
        await api.CompleteAsync(uploadId, transfer.FileSha256, notify: true, cancellationToken).ConfigureAwait(false);
        progress.WriteEvent("assembling", new Dictionary<string, object?>
        {
            ["uploadId"] = uploadId,
            ["fileSha256"] = transfer.FileSha256,
        });

        var final = await WaitForReadyAsync(api, uploadId, cancellationToken).ConfigureAwait(false);

        await store.SetStateAsync(uploadId, UploadStates.Ready, cancellationToken).ConfigureAwait(false);

        progress.Report(force: true);
        progress.ReportSummary(new Dictionary<string, object?>
        {
            ["uploadId"] = uploadId,
            ["vault"] = vaultSlug,
            ["vaultPath"] = final.VaultPath,
            ["fileSha256"] = final.FinalSha256 ?? transfer.FileSha256,
            ["parts"] = $"{transfer.PartsSent} sent, {skip.Count} skipped, {totalParts} total",
            ["chunkSize"] = $"{_settings.ChunkSizeMiB} MiB",
            ["compression"] = compressionEnabled ? "zstd (level 3) with 95% adaptive cutoff" : "disabled",
        });

        return new UploadResult
        {
            UploadId = uploadId,
            VaultSlug = vaultSlug,
            VaultPath = final.VaultPath,
            FinalSha256 = final.FinalSha256 ?? transfer.FileSha256,
            BytesSent = transfer.BytesSent,
            PartsSent = transfer.PartsSent,
            PartsSkipped = skip.Count,
            DurationSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            CompressionEnabled = compressionEnabled,
            ChunkSize = chunkSize,
            TotalParts = totalParts,
        };
    }

    private sealed record TransferOutcome(string FileSha256, long BytesSent, int PartsSent);

    private async Task<TransferOutcome> TransferPartsAsync(
        Protocol.AuroraApiClient api,
        ResumeStateStore store,
        ProgressReporter progress,
        TokenBucket bucket,
        string uploadId,
        string filePath,
        long totalBytes,
        int chunkSize,
        int totalParts,
        bool compressionEnabled,
        HashSet<int> skip,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<PreparedPart>(
            new BoundedChannelOptions(Math.Max(2, _settings.Concurrency * 2))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            });

        using var errorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = ReadPartsAsync(
            channel.Writer,
            filePath,
            totalBytes,
            chunkSize,
            compressionEnabled,
            skip,
            errorCts.Token);

        var workers = new List<Task>(_settings.Concurrency);
        for (int i = 0; i < _settings.Concurrency; i++)
        {
            workers.Add(WorkerAsync(channel.Reader, api, store, progress, bucket, uploadId, errorCts.Token));
        }

        var workerTask = Task.WhenAll(workers);
        string? fileSha256;
        try
        {
            await Task.WhenAll(reader, workerTask).ConfigureAwait(false);
            fileSha256 = reader.Result.FileSha256;
        }
        catch
        {
            errorCts.Cancel();
            var failure = FirstRealError(reader, workerTask);
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            throw;
        }

        return new TransferOutcome(fileSha256!, progress.BytesSent, progress.PartsDone);
    }

    private static Exception? FirstRealError(params Task[] tasks)
    {
        foreach (Task task in tasks)
        {
            if (!task.IsFaulted || task.Exception is null)
            {
                continue;
            }

            foreach (Exception inner in task.Exception.InnerExceptions)
            {
                if (inner is not OperationCanceledException)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the file sequentially, hashes it incrementally and prepares the parts that still have to
    /// be uploaded. Parts already confirmed by the receiver are read and hashed but not sent.
    /// </summary>
    private async Task<ReaderOutcome> ReadPartsAsync(
        ChannelWriter<PreparedPart> writer,
        string filePath,
        long totalBytes,
        int chunkSize,
        bool compressionEnabled,
        HashSet<int> skip,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var globalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var compressor = new ZstdSharp.Compressor(CompressionCodec.Level);
        long bytesTransferred = 0;

        try
        {
            foreach (ChunkDescriptor descriptor in ChunkPlan.DescribeAll(totalBytes, chunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rawLength = descriptor.Length;
                byte[]? raw = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkSize);
                byte[]? compressed = null;

                try
                {
                    await ReadExactlyAsync(stream, raw.AsMemory(0, rawLength), cancellationToken).ConfigureAwait(false);
                    globalHash.AppendData(raw, 0, rawLength);

                    if (skip.Contains(descriptor.PartNumber))
                    {
                        continue;
                    }

                    string rawHash = CompressionCodec.Sha256Hex(raw, 0, rawLength);
                    byte[] body;
                    int bodyLength;
                    string compression;
                    string chunkHash;

                    if (compressionEnabled)
                    {
                        compressed = CompressionCodec.Compress(raw, 0, rawLength, out int compressedLength, compressor);
                        if (CompressionPolicy.ShouldSendUncompressed(rawLength, compressedLength))
                        {
                            // The compressed chunk is not worth it: send the raw bytes instead.
                            body = raw;
                            bodyLength = rawLength;
                            compression = "none";
                            chunkHash = rawHash;
                            raw = null;
                            System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
                            compressed = null;
                        }
                        else
                        {
                            body = compressed;
                            bodyLength = compressedLength;
                            compression = "zstd";
                            chunkHash = CompressionCodec.Sha256Hex(compressed, 0, compressedLength);
                            compressed = null;
                            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
                            raw = null;
                        }
                    }
                    else
                    {
                        body = raw;
                        bodyLength = rawLength;
                        compression = "none";
                        chunkHash = rawHash;
                        raw = null;
                    }

                    bytesTransferred += bodyLength;

                    await writer.WriteAsync(
                        new PreparedPart
                        {
                            PartNumber = descriptor.PartNumber,
                            Body = body,
                            BodyLength = bodyLength,
                            RawLength = rawLength,
                            Compression = compression,
                            ChunkSha256 = chunkHash,
                            RawSha256 = rawHash,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (raw is not null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(raw);
                    }

                    if (compressed is not null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
                    }
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }

        string digest = Convert.ToHexString(globalHash.GetHashAndReset()).ToLowerInvariant();
        return new ReaderOutcome(digest, bytesTransferred);
    }

    private sealed record ReaderOutcome(string FileSha256, long BytesTransferred);

    private static async Task WorkerAsync(
        ChannelReader<PreparedPart> reader,
        Protocol.AuroraApiClient api,
        ResumeStateStore store,
        ProgressReporter progress,
        TokenBucket bucket,
        string uploadId,
        CancellationToken cancellationToken)
    {
        await foreach (PreparedPart part in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            using (part)
            {
                await api.PutPartAsync(
                    uploadId,
                    part.PartNumber,
                    part.Body,
                    0,
                    part.BodyLength,
                    part.ChunkSha256,
                    part.RawSha256,
                    part.Compression,
                    part.RawLength,
                    bucket,
                    cancellationToken).ConfigureAwait(false);

                await store.SavePartAsync(
                    uploadId,
                    part.PartNumber,
                    part.Compression,
                    part.BodyLength,
                    part.RawLength,
                    part.ChunkSha256,
                    cancellationToken).ConfigureAwait(false);

                progress.OnPartCompleted(part.BodyLength);
            }
        }
    }

    private async Task<Protocol.UploadSession> WaitForReadyAsync(
        Protocol.AuroraApiClient api,
        string uploadId,
        CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(2);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StatusTimeout;

        while (true)
        {
            Protocol.UploadSession status = await api.GetStatusAsync(uploadId, cancellationToken).ConfigureAwait(false);

            switch (status.Status)
            {
                case "READY":
                    return status;
                case "FAILED":
                    throw new Protocol.ApiException(
                        HttpStatusCode.InternalServerError,
                        status.Error?.Code ?? "global_hash_mismatch",
                        status.Error?.Message ?? "The receiver could not assemble the file.");
                case "EXPIRED":
                    throw new Protocol.ApiException(
                        HttpStatusCode.NotFound,
                        "expired",
                        "The upload session expired on the receiver.");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"The upload '{uploadId}' was not ready after {StatusTimeout.TotalHours:F0} hours.");
            }

            await _delay.DelayAsync(interval, cancellationToken).ConfigureAwait(false);
            interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * 1.5, 15));
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The file ended before the expected chunk was complete.");
            }

            total += read;
        }
    }
}
