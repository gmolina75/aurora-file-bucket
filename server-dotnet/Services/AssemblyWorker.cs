using System.Buffers;
using System.Security.Cryptography;
using AuroraFileBucket.Server.Configuration;
using AuroraFileBucket.Server.Data;
using AuroraFileBucket.Server.Infrastructure;
using AuroraFileBucket.Server.Services.Notifications;
using Microsoft.Extensions.Options;

namespace AuroraFileBucket.Server.Services;

/// <summary>
/// Background worker that performs the assembly described in PROTOCOL.md 2.5:
/// read the parts in order from staging, decompress them when needed, write the vault
/// file while hashing the uncompressed stream and only publish the file when the
/// global SHA-256 matches.
/// </summary>
public sealed class AssemblyWorker : BackgroundService
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly AssemblyQueue _queue;
    private readonly UploadRepository _sessions;
    private readonly StorageLayout _layout;
    private readonly AuroraStorageOptions _options;
    private readonly NotificationsOptions _notifications;
    private readonly IProgressPublisher _progress;
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly TimeProvider _clock;
    private readonly ILogger<AssemblyWorker> _logger;

    public AssemblyWorker(
        AssemblyQueue queue,
        UploadRepository sessions,
        StorageLayout layout,
        IOptions<AuroraStorageOptions> options,
        IOptions<NotificationsOptions> notifications,
        IProgressPublisher progress,
        IEnumerable<INotifier> notifiers,
        TimeProvider clock,
        ILogger<AssemblyWorker> logger)
    {
        _queue = queue;
        _sessions = sessions;
        _layout = layout;
        _options = options.Value;
        _notifications = notifications.Value;
        _progress = progress;
        _notifiers = notifiers;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await AssembleAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembly of upload {UploadId} failed unexpectedly.", job.UploadId);
                await TryMarkFailedAsync(job.UploadId, ApiError.Codes.InternalError, "The assembly failed unexpectedly.", cancellationToken: stoppingToken);
            }
        }
    }

    private async Task AssembleAsync(AssemblyJob job, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetSessionAsync(job.UploadId, cancellationToken);
        if (session is null)
        {
            _logger.LogWarning("Upload {UploadId} no longer exists; the assembly job is dropped.", job.UploadId);
            return;
        }

        var vault = _layout.FindVault(session.VaultSlug);
        if (vault is null)
        {
            await TryMarkFailedAsync(job.UploadId, ApiError.Codes.InvalidState, $"The vault '{session.VaultSlug}' is not configured.", cancellationToken);
            return;
        }

        var parts = await _sessions.GetPartsAsync(job.UploadId, cancellationToken);
        if (parts.Count != session.TotalParts)
        {
            await TryMarkFailedAsync(
                job.UploadId,
                ApiError.Codes.IncompleteUpload,
                $"Only {parts.Count} of {session.TotalParts} parts are present.",
                cancellationToken);
            return;
        }

        var finalName = ResolveFinalName(vault.Path, session.FileName, session.UploadId);
        var finalPath = Path.Combine(vault.Path, finalName);
        var tempPath = Path.Combine(vault.Path, finalName + ".assembling");

        string digest;
        try
        {
            digest = await WriteAssembledFileAsync(session, parts, vault, tempPath, cancellationToken);
        }
        catch (AssemblyVerificationException ex)
        {
            TryDelete(tempPath);
            _logger.LogWarning(
                "Assembly of {UploadId} rejected: {Code}. {Message}",
                session.UploadId,
                ex.Code,
                ex.Message);
            await TryMarkFailedAsync(session.UploadId, ex.Code, ex.Message, cancellationToken);
            await PublishStatusAsync(session, ex.Code, cancellationToken);
            await NotifyAsync(job, session, null, ex.Code, ex.Message, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            _logger.LogError(ex, "Assembly of {UploadId} failed while writing {Path}.", session.UploadId, tempPath);
            await TryMarkFailedAsync(session.UploadId, ApiError.Codes.InternalError, "The assembly could not write the vault file.", cancellationToken);
            return;
        }

        var expected = session.ExpectedSha256;
        if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, digest, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(tempPath);
            _logger.LogWarning(
                "Assembly of {UploadId} rejected: the global SHA-256 {Actual} does not match the expected {Expected}.",
                session.UploadId,
                digest,
                expected);
            await TryMarkFailedAsync(session.UploadId, ApiError.Codes.GlobalHashMismatch, "The global SHA-256 does not match the expected value.", cancellationToken);
            await PublishStatusAsync(session, ApiError.Codes.GlobalHashMismatch, cancellationToken);
            await NotifyAsync(job, session, null, ApiError.Codes.GlobalHashMismatch, "The global SHA-256 does not match the expected value.", cancellationToken);
            return;
        }

        // Atomic publication inside the vault volume.
        File.Move(tempPath, finalPath, overwrite: true);

        if (_options.ImmediatePostAssemblyCleanup)
        {
            DeleteStagingFolder(session.UploadId);
        }

        await _sessions.MarkReadyAsync(
            session.UploadId,
            finalPath,
            digest,
            ProtocolTime.ToProtocol(_clock.GetUtcNow()),
            cancellationToken);

        _logger.LogInformation(
            "Upload {UploadId} assembled into {Path} (sha256 {Sha256}).",
            session.UploadId,
            finalPath,
            digest);

        await PublishStatusAsync(session, null, cancellationToken);
        await NotifyAsync(job, session, finalPath, null, null, cancellationToken);
    }

    private async Task<string> WriteAssembledFileAsync(
        UploadSessionRow session,
        IReadOnlyList<UploadPartRow> parts,
        VaultDefinition vault,
        string tempPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(vault.Path);

        await using var output = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            useAsync: true);

        using var globalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var part in parts)
        {
            var partPath = _layout.StagingPartPath(session.UploadId, part.PartNumber);
            if (!File.Exists(partPath))
            {
                throw new AssemblyVerificationException(
                    ApiError.Codes.IncompleteUpload,
                    $"The staged file of part {part.PartNumber} is missing.");
            }

            await using var file = new FileStream(
                partPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                useAsync: true);

            Stream source = file;
            Stream? decompressor = null;
            if (string.Equals(part.Compression, "zstd", StringComparison.OrdinalIgnoreCase))
            {
                decompressor = new ZstdSharp.DecompressionStream(file);
                source = decompressor;
            }

            using var rawHash = string.IsNullOrEmpty(part.RawSha256)
                ? null
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            try
            {
                await CopyAsync(source, output, globalHash, rawHash, cancellationToken);
            }
            finally
            {
                if (decompressor is not null)
                {
                    await decompressor.DisposeAsync();
                }
            }

            if (rawHash is not null && !string.IsNullOrEmpty(part.RawSha256))
            {
                var rawDigest = Convert.ToHexString(rawHash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(rawDigest, part.RawSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AssemblyVerificationException(
                        "part_hash_mismatch",
                        $"The decompressed content of part {part.PartNumber} does not match its X-Raw-Sha256.");
                }
            }
        }

        await output.FlushAsync(cancellationToken);
        return Convert.ToHexString(globalHash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        IncrementalHash globalHash,
        IncrementalHash? rawHash,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                globalHash.AppendData(buffer, 0, read);
                rawHash?.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Destination name of the assembled file. When the name is taken the upload id is
    /// inserted before the extension (PROTOCOL.md 2.5 step 2).
    /// </summary>
    internal static string ResolveFinalName(string vaultPath, string fileName, string uploadId)
    {
        if (!File.Exists(Path.Combine(vaultPath, fileName)))
        {
            return fileName;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return $"{name}.{uploadId}{extension}";
    }

    private void DeleteStagingFolder(string uploadId)
    {
        var folder = _layout.StagingFolderFor(uploadId);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the staging folder {Folder}.", folder);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the orphan purge removes it later.
        }
    }

    private async Task TryMarkFailedAsync(string uploadId, string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _sessions.MarkFailedAsync(uploadId, code, message, ProtocolTime.ToProtocol(_clock.GetUtcNow()), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark upload {UploadId} as FAILED.", uploadId);
        }
    }

    private async Task PublishStatusAsync(UploadSessionRow session, string? errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await _progress.StatusChangedAsync(
                new UploadEvent(
                    session.UploadId,
                    "status",
                    errorCode is null ? "READY" : "FAILED",
                    ReceivedParts: session.TotalParts,
                    TotalParts: session.TotalParts,
                    FileName: session.FileName,
                    VaultSlug: session.VaultSlug,
                    ErrorCode: errorCode),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not publish the status change of {UploadId}.", session.UploadId);
        }
    }

    private async Task NotifyAsync(
        AssemblyJob job,
        UploadSessionRow session,
        string? vaultPath,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (!job.Notify)
        {
            return;
        }

        var success = errorCode is null;
        if (success ? !_notifications.NotifyOnSuccess : !_notifications.NotifyOnFailure)
        {
            return;
        }

        var message = new NotificationMessage(
            success,
            session.UploadId,
            session.FileName,
            session.VaultSlug,
            session.TotalBytes,
            null,
            vaultPath,
            errorCode,
            errorMessage,
            _clock.GetUtcNow());

        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.SendAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The {Channel} notification of upload {UploadId} failed.", notifier.Channel, session.UploadId);
            }
        }
    }
}

/// <summary>Raised when the assembled content does not satisfy the expected integrity.</summary>
public sealed class AssemblyVerificationException : Exception
{
    public AssemblyVerificationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
