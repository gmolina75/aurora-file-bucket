using AuroraFileBucket.Server.Configuration;
using AuroraFileBucket.Server.Data;
using AuroraFileBucket.Server.Services;
using Microsoft.Extensions.Options;

namespace AuroraFileBucket.Server.Services;

/// <summary>
/// Scheduled clean up (PROTOCOL.md 2.6): sessions without activity for longer than
/// <c>AuroraStorage:OrphanSessionHours</c> are marked EXPIRED and their staging folder
/// is deleted. Staging folders that no longer belong to a known session are removed too.
/// </summary>
public sealed class OrphanPurgeService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    private readonly UploadRepository _sessions;
    private readonly StorageLayout _layout;
    private readonly AuroraStorageOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OrphanPurgeService> _logger;

    public OrphanPurgeService(
        UploadRepository sessions,
        StorageLayout layout,
        IOptions<AuroraStorageOptions> options,
        TimeProvider clock,
        ILogger<OrphanPurgeService> logger)
    {
        _sessions = sessions;
        _layout = layout;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the application a moment to start before the first sweep.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(RunInterval);
        do
        {
            try
            {
                await PurgeAsync(_clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The orphan purge sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs one sweep. Sessions older than the configured window are expired.</summary>
    public async Task PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hours = _options.OrphanSessionHours > 0 ? _options.OrphanSessionHours : 72;
        var cutoff = now.AddHours(-hours);

        var stale = await _sessions.GetStaleActiveSessionsAsync(cutoff, cancellationToken).ConfigureAwait(false);
        foreach (var session in stale)
        {
            DeleteFolder(_layout.StagingFolderFor(session.UploadId));
            await _sessions.MarkExpiredAsync(session.UploadId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Upload {UploadId} expired after {Hours} hours without activity; its staging folder was removed.",
                session.UploadId,
                hours);
        }

        await RemoveOrphanFoldersAsync(stale, cutoff, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveOrphanFoldersAsync(
        IReadOnlyList<UploadSessionRow> stale,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_layout.StagingRoot))
        {
            return;
        }

        DirectoryInfo[] folders;
        try
        {
            folders = new DirectoryInfo(_layout.StagingRoot).GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list the staging root {StagingRoot}.", _layout.StagingRoot);
            return;
        }

        foreach (var folder in folders)
        {
            if (stale.Any(s => string.Equals(s.UploadId, folder.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var session = await _sessions.GetSessionAsync(folder.Name, cancellationToken).ConfigureAwait(false);
            if (session is not null && session.Status is not ("READY" or "FAILED" or "EXPIRED"))
            {
                // Still an active session: it is handled by the stale session sweep.
                continue;
            }

            if (folder.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                DeleteFolder(folder.FullName);
                _logger.LogInformation("Removed the orphan staging folder {Folder}.", folder.FullName);
            }
        }
    }

    private void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the staging folder {Path}.", path);
        }
    }
}
