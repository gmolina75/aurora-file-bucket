using System.Security.Cryptography;
using System.Buffers;
using AuroraFileBucket.Server.Contracts;
using AuroraFileBucket.Server.Data;
using AuroraFileBucket.Server.Infrastructure;
using AuroraFileBucket.Server.Services;
using Microsoft.Extensions.Options;

namespace AuroraFileBucket.Server.Endpoints;

/// <summary>
/// The upload lifecycle: <c>POST /v1/uploads/init</c>, <c>PUT /v1/uploads/{id}/parts/{n}</c>,
/// <c>GET /v1/uploads/{id}/status</c> and <c>POST /v1/uploads/{id}/complete</c>.
/// </summary>
public static class UploadEndpoints
{
    private const int CopyBufferSize = 64 * 1024;
    private const int MaxMissingSample = 20;

    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/uploads").RequireAuthorization().WithTags("uploads");

        group.MapPost("/init", InitAsync)
            .WithName("InitUpload");

        group.MapPut("/{uploadId}/parts/{partNumber:int}", PutPartAsync)
            .WithName("PutUploadPart");

        group.MapGet("/{uploadId}/status", GetStatusAsync)
            .WithName("GetUploadStatus");

        group.MapPost("/{uploadId}/complete", CompleteAsync)
            .WithName("CompleteUpload");
    }

    private static async Task<IResult> InitAsync(
        InitRequest? request,
        HttpContext http,
        UploadRepository sessions,
        StorageLayout layout,
        VaultQuotaService quota,
        DiskPressureMonitor pressure,
        IOptions<Configuration.AuroraStorageOptions> storageOptions,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AuroraFileBucket.Server.Uploads");
        var options = storageOptions.Value;

        // --- manifest validation -------------------------------------------------
        var fileName = request?.FileName?.Trim() ?? string.Empty;
        if (!ManifestValidator.IsValidFileName(fileName))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidManifest,
                "fileName must be a plain file name of 1 to 255 characters without path separators.");
        }

        var totalBytes = request?.TotalBytes ?? 0;
        if (totalBytes <= 0)
        {
            return ApiError.BadRequest(ApiError.Codes.InvalidManifest, "totalBytes must be greater than zero.");
        }

        var chunkSize = request?.ChunkSize ?? 0;
        var minChunk = (long)options.MinChunkSizeMiB * 1024 * 1024;
        var maxChunk = (long)options.MaxChunkSizeMiB * 1024 * 1024;
        if (chunkSize < minChunk || chunkSize > maxChunk)
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidManifest,
                $"chunkSize must be between {minChunk} and {maxChunk} bytes.",
                new Dictionary<string, object?>
                {
                    ["minChunkSize"] = minChunk,
                    ["maxChunkSize"] = maxChunk,
                    ["chunkSize"] = chunkSize
                });
        }

        var totalParts = request?.TotalParts ?? 0;
        var expectedParts = ManifestValidator.ExpectedPartCount(totalBytes, chunkSize);
        if (totalParts != expectedParts || totalParts < 1)
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidManifest,
                $"totalParts must be exactly {expectedParts} for {totalBytes} bytes with a chunk size of {chunkSize} bytes.",
                new Dictionary<string, object?>
                {
                    ["expectedTotalParts"] = expectedParts,
                    ["totalParts"] = totalParts
                });
        }

        var fileSha256 = string.IsNullOrWhiteSpace(request?.FileSha256) ? null : request!.FileSha256!.Trim();
        if (fileSha256 is not null && !ManifestValidator.IsSha256Hex(fileSha256))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidManifest,
                "fileSha256 must be 64 lowercase hexadecimal characters.");
        }

        var compression = request?.Compression?.Trim();
        if (!string.IsNullOrEmpty(compression) && compression is not ("auto" or "none"))
        {
            return ApiError.BadRequest(ApiError.Codes.InvalidManifest, "compression must be 'auto' or 'none'.");
        }

        // --- vault isolation -----------------------------------------------------
        var requestedSlug = http.Request.Headers["X-Vault-Slug"].FirstOrDefault()?.Trim();
        var allowedVaults = http.User.GetVaults();

        string vaultSlug;
        if (!string.IsNullOrWhiteSpace(requestedSlug))
        {
            if (!allowedVaults.Any(v => string.Equals(v, requestedSlug, StringComparison.OrdinalIgnoreCase)))
            {
                return ApiError.Forbidden(
                    ApiError.Codes.VaultNotAllowed,
                    $"The token is not allowed to write to the vault '{requestedSlug}'.");
            }

            vaultSlug = allowedVaults.First(v => string.Equals(v, requestedSlug, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var candidate = allowedVaults
                .Select(slug => layout.FindVault(slug))
                .FirstOrDefault(v => v is not null);
            if (candidate is null)
            {
                return ApiError.Forbidden(
                    ApiError.Codes.VaultNotAllowed,
                    "The token does not have access to any configured vault.");
            }

            vaultSlug = candidate.Slug;
        }

        var vault = layout.FindVault(vaultSlug);
        if (vault is null)
        {
            return ApiError.Forbidden(
                ApiError.Codes.VaultNotAllowed,
                $"The vault '{vaultSlug}' is not configured on this receiver.");
        }

        // --- quota ---------------------------------------------------------------
        if (quota.WouldExceedQuota(vault, totalBytes, out var usageBytes))
        {
            return ApiError.PayloadTooLarge(
                ApiError.Codes.QuotaExceeded,
                $"The vault '{vaultSlug}' has no room for {totalBytes} more bytes.",
                new Dictionary<string, object?>
                {
                    ["vaultSlug"] = vault.Slug,
                    ["quotaBytes"] = vault.QuotaBytes,
                    ["usedBytes"] = usageBytes,
                    ["requestedBytes"] = totalBytes
                });
        }

        // --- backpressure --------------------------------------------------------
        if (pressure.IsUnderPressure())
        {
            return ApiError.Backpressure(http, pressure.RetryAfterSeconds, "The receiver storage is above the backpressure threshold.");
        }

        // --- create the session --------------------------------------------------
        var uploadId = Guid.NewGuid().ToString("D");
        var now = clock.GetUtcNow();
        var startedAt = ProtocolTime.ToProtocol(now);
        var expiresAt = ProtocolTime.ToProtocol(now.AddHours(options.OrphanSessionHours > 0 ? options.OrphanSessionHours : 72));

        var session = new UploadSessionRow
        {
            UploadId = uploadId,
            VaultSlug = vault.Slug,
            FileName = fileName,
            TotalBytes = totalBytes,
            ChunkSize = chunkSize,
            TotalParts = totalParts,
            ExpectedSha256 = fileSha256,
            Status = "INIT",
            ClientId = http.Request.Headers["X-Client-Id"].FirstOrDefault(),
            ClientPlatform = http.Request.Headers["X-Client-Platform"].FirstOrDefault(),
            StartedAt = startedAt,
            ExpiresAt = expiresAt
        };

        await sessions.InsertSessionAsync(session, cancellationToken);

        try
        {
            Directory.CreateDirectory(layout.StagingFolderFor(uploadId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not create the staging folder of upload {UploadId}.", uploadId);
            return ApiError.Result(StatusCodes.Status500InternalServerError, ApiError.Codes.InternalError, "The staging folder could not be created.");
        }

        var missing = Enumerable.Range(1, totalParts).ToList();
        return Results.Json(
            new UploadSessionCreated(
                uploadId,
                "INIT",
                vault.Slug,
                fileName,
                totalBytes,
                chunkSize,
                totalParts,
                Array.Empty<int>(),
                missing.Take(100).ToList(),
                expiresAt),
            statusCode: StatusCodes.Status201Created,
            contentType: "application/json; charset=utf-8");
    }

    private static async Task<IResult> PutPartAsync(
        string uploadId,
        int partNumber,
        HttpContext http,
        UploadRepository sessions,
        StorageLayout layout,
        DiskPressureMonitor pressure,
        IProgressPublisher progress,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AuroraFileBucket.Server.Parts");

        var session = await sessions.GetSessionAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiError.NotFound();
        }

        if (!http.User.IsVaultAllowed(session.VaultSlug))
        {
            return ApiError.Forbidden(
                ApiError.Codes.VaultNotAllowed,
                $"The token is not allowed to write to the vault '{session.VaultSlug}'.");
        }

        if (session.Status is "READY" or "ASSEMBLING" or "EXPIRED")
        {
            return ApiError.Conflict(
                ApiError.Codes.InvalidState,
                $"The upload is in state '{session.Status}' and does not accept more parts.");
        }

        if (partNumber < 1 || partNumber > session.TotalParts)
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                $"partNumber must be between 1 and {session.TotalParts}.");
        }

        // --- mandatory headers ---------------------------------------------------
        var chunkSha256 = http.Request.Headers["X-Chunk-Sha256"].FirstOrDefault()?.Trim();
        if (!ManifestValidator.IsSha256HexAnyCase(chunkSha256))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "The X-Chunk-Sha256 header is mandatory and must contain 64 hexadecimal characters.");
        }

        var compression = http.Request.Headers["X-Compression"].FirstOrDefault()?.Trim().ToLowerInvariant();
        if (compression is not ("zstd" or "none"))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "The X-Compression header is mandatory and must be 'zstd' or 'none'.");
        }

        if (!ManifestValidator.TryParseHeaderInt(http.Request.Headers["X-Raw-Size"].FirstOrDefault(), out var rawSize) || rawSize < 0)
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "The X-Raw-Size header is mandatory and must be a non negative integer.");
        }

        var rawSha256 = http.Request.Headers["X-Raw-Sha256"].FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(rawSha256) && !ManifestValidator.IsSha256HexAnyCase(rawSha256))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "The X-Raw-Sha256 header must contain 64 hexadecimal characters.");
        }

        if (!http.Request.ContentLength.HasValue)
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "The Content-Length header is mandatory: chunked transfer encoding is not accepted.");
        }

        if (http.Request.Headers.TransferEncoding.Any(value => value?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "Chunked transfer encoding is not accepted: the Content-Length header is mandatory.");
        }

        var contentType = http.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidRequest,
                "The Content-Type header must be application/octet-stream.");
        }

        // --- backpressure --------------------------------------------------------
        if (pressure.IsUnderPressure())
        {
            return ApiError.Backpressure(http, pressure.RetryAfterSeconds, "The receiver storage is above the backpressure threshold.");
        }

        // --- idempotency ---------------------------------------------------------
        var existing = await sessions.GetPartAsync(uploadId, partNumber, cancellationToken);
        if (existing is not null
            && !string.Equals(existing.ChunkSha256, chunkSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ApiError.Conflict(
                ApiError.Codes.PartConflict,
                $"Part {partNumber} is already stored with a different hash.",
                new Dictionary<string, object?>
                {
                    ["partNumber"] = partNumber,
                    ["storedSha256"] = existing.ChunkSha256,
                    ["receivedSha256"] = chunkSha256
                });
        }

        // --- stream the chunk straight to disk ------------------------------------
        var folder = layout.StagingFolderFor(uploadId);
        var partPath = layout.StagingPartPath(uploadId, partNumber);

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not create the staging folder {Folder}.", folder);
            return ApiError.Result(StatusCodes.Status500InternalServerError, ApiError.Codes.InternalError, "The staging folder could not be created.");
        }

        long compressedSize;
        string computedHash;
        try
        {
            (compressedSize, computedHash) = await StreamToStagingAsync(http, partPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The transfer of part {PartNumber} of upload {UploadId} was interrupted.", partNumber, uploadId);
            TryDeleteFile(partPath);
            await sessions.DeletePartAsync(uploadId, partNumber, cancellationToken);
            throw;
        }

        if (!string.Equals(computedHash, chunkSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partPath);
            logger.LogWarning(
                "Part {PartNumber} of upload {UploadId} was rejected: the body hash {Actual} does not match X-Chunk-Sha256 {Expected}.",
                partNumber,
                uploadId,
                computedHash,
                chunkSha256);
            return ApiError.BadRequest(
                ApiError.Codes.ChunkHashMismatch,
                "The SHA-256 of the received body does not match the X-Chunk-Sha256 header.",
                new Dictionary<string, object?>
                {
                    ["expected"] = chunkSha256,
                    ["actual"] = computedHash
                });
        }

        var part = new UploadPartRow
        {
            UploadId = uploadId,
            PartNumber = partNumber,
            RawOffset = (long)(partNumber - 1) * session.ChunkSize,
            RawSize = rawSize,
            CompressedSize = compressedSize,
            Compression = compression,
            ChunkSha256 = computedHash,
            RawSha256 = string.IsNullOrEmpty(rawSha256) ? null : rawSha256,
            ConfirmedAt = ProtocolTime.ToProtocol(clock.GetUtcNow())
        };

        await sessions.UpsertPartAsync(part, cancellationToken);

        if (session.Status == "INIT")
        {
            await sessions.SetStatusAsync(uploadId, "UPLOADING", cancellationToken);
            session = session with { Status = "UPLOADING" };
        }

        var received = await sessions.GetPartsAsync(uploadId, cancellationToken);
        var receivedCount = received.Count;

        try
        {
            await progress.PartConfirmedAsync(
                new UploadEvent(
                    uploadId,
                    "part",
                    session.Status,
                    PartNumber: partNumber,
                    ReceivedParts: receivedCount,
                    TotalParts: session.TotalParts,
                    BytesReceived: received.Sum(p => p.CompressedSize),
                    FileName: session.FileName,
                    VaultSlug: session.VaultSlug),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not publish the progress of upload {UploadId}.", uploadId);
        }

        return Results.Json(
            new PartReceipt(
                partNumber,
                compressedSize,
                rawSize,
                computedHash,
                compression,
                receivedCount,
                session.TotalParts,
                session.Status),
            statusCode: StatusCodes.Status200OK,
            contentType: "application/json; charset=utf-8");
    }

    private static async Task<IResult> GetStatusAsync(
        string uploadId,
        HttpContext http,
        UploadRepository sessions,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetSessionAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiError.NotFound();
        }

        if (!http.User.IsVaultAllowed(session.VaultSlug))
        {
            return ApiError.Forbidden(
                ApiError.Codes.VaultNotAllowed,
                $"The token is not allowed to read the vault '{session.VaultSlug}'.");
        }

        return Results.Json(
            await BuildStatusAsync(sessions, session, cancellationToken),
            statusCode: StatusCodes.Status200OK,
            contentType: "application/json; charset=utf-8");
    }

    private static async Task<IResult> CompleteAsync(
        string uploadId,
        CompleteRequest? request,
        HttpContext http,
        UploadRepository sessions,
        StorageLayout layout,
        AssemblyQueue queue,
        IProgressPublisher progress,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AuroraFileBucket.Server.Uploads");

        var session = await sessions.GetSessionAsync(uploadId, cancellationToken);
        if (session is null)
        {
            return ApiError.NotFound();
        }

        if (!http.User.IsVaultAllowed(session.VaultSlug))
        {
            return ApiError.Forbidden(
                ApiError.Codes.VaultNotAllowed,
                $"The token is not allowed to write to the vault '{session.VaultSlug}'.");
        }

        // The hash sent here wins over the one of /init.
        var fileSha256 = string.IsNullOrWhiteSpace(request?.FileSha256) ? null : request!.FileSha256!.Trim();
        if (fileSha256 is not null && !ManifestValidator.IsSha256Hex(fileSha256))
        {
            return ApiError.BadRequest(
                ApiError.Codes.InvalidManifest,
                "fileSha256 must be 64 lowercase hexadecimal characters.");
        }

        if (fileSha256 is not null && !string.Equals(fileSha256, session.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            await sessions.SetExpectedSha256Async(uploadId, fileSha256, cancellationToken);
            session = session with { ExpectedSha256 = fileSha256 };
        }

        if (session.Status == "READY")
        {
            return Results.Json(
                new AssemblyAccepted(uploadId, "READY", $"/v1/uploads/{uploadId}/status"),
                statusCode: StatusCodes.Status202Accepted,
                contentType: "application/json; charset=utf-8");
        }

        if (session.Status == "ASSEMBLING")
        {
            return Results.Json(
                new AssemblyAccepted(uploadId, "ASSEMBLING", $"/v1/uploads/{uploadId}/status"),
                statusCode: StatusCodes.Status202Accepted,
                contentType: "application/json; charset=utf-8");
        }

        var parts = await sessions.GetPartsAsync(uploadId, cancellationToken);
        var received = parts.Select(p => p.PartNumber).ToHashSet();
        var missing = Enumerable.Range(1, session.TotalParts).Where(number => !received.Contains(number)).ToList();

        if (missing.Count > 0)
        {
            return ApiError.Conflict(
                ApiError.Codes.IncompleteUpload,
                $"{missing.Count} parts are still missing.",
                new Dictionary<string, object?>
                {
                    ["missingCount"] = missing.Count,
                    ["missing"] = missing.Take(MaxMissingSample).ToList(),
                    ["totalParts"] = session.TotalParts
                });
        }

        await sessions.SetStatusAsync(uploadId, "ASSEMBLING", cancellationToken);

        var notify = request?.Notify ?? true;
        if (!queue.TryEnqueue(new AssemblyJob(uploadId, notify)))
        {
            await sessions.SetStatusAsync(uploadId, session.Status, cancellationToken);
            return ApiError.Result(StatusCodes.Status500InternalServerError, ApiError.Codes.InternalError, "The assembly could not be queued.");
        }

        logger.LogInformation("Assembly of upload {UploadId} queued ({TotalParts} parts).", uploadId, session.TotalParts);

        try
        {
            await progress.StatusChangedAsync(
                new UploadEvent(
                    uploadId,
                    "status",
                    "ASSEMBLING",
                    ReceivedParts: session.TotalParts,
                    TotalParts: session.TotalParts,
                    FileName: session.FileName,
                    VaultSlug: session.VaultSlug),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not publish the status change of upload {UploadId}.", uploadId);
        }

        return Results.Json(
            new AssemblyAccepted(uploadId, "ASSEMBLING", $"/v1/uploads/{uploadId}/status"),
            statusCode: StatusCodes.Status202Accepted,
            contentType: "application/json; charset=utf-8");
    }

    /// <summary>Builds the status document. The server is the authority on the parts.</summary>
    internal static async Task<UploadSessionStatus> BuildStatusAsync(
        UploadRepository sessions,
        UploadSessionRow session,
        CancellationToken cancellationToken)
    {
        var parts = await sessions.GetPartsAsync(session.UploadId, cancellationToken);
        var received = parts.Select(p => p.PartNumber).OrderBy(number => number).ToList();
        var receivedSet = received.ToHashSet();
        var missing = Enumerable.Range(1, session.TotalParts).Where(number => !receivedSet.Contains(number)).ToList();

        ErrorBody? error = null;
        if (!string.IsNullOrEmpty(session.ErrorCode))
        {
            error = new ErrorBody(session.ErrorCode!, session.ErrorMessage ?? "The upload failed.");
        }

        return new UploadSessionStatus(
            session.UploadId,
            session.Status,
            session.VaultSlug,
            session.FileName,
            session.TotalBytes,
            session.ChunkSize,
            session.TotalParts,
            received,
            missing.Take(100).ToList(),
            parts.Sum(p => p.CompressedSize),
            session.StartedAt,
            session.ExpiresAt,
            session.CompletedAt,
            session.VaultPath,
            session.FinalSha256,
            error);
    }

    /// <summary>
    /// Streams the request body straight into the staging file while computing the
    /// SHA-256 of the received bytes. Nothing but a 64 KiB buffer is kept in memory.
    /// </summary>
    private static async Task<(long Size, string Sha256)> StreamToStagingAsync(
        HttpContext http,
        string partPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var file = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }

            await file.FlushAsync(cancellationToken);
            return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteFile(string path)
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
            // Best effort: the orphan purge removes the leftovers.
        }
    }
}
