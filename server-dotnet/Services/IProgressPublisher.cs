namespace AuroraFileBucket.Server.Services;

/// <summary>Upload event published to the dashboard (SignalR).</summary>
public sealed record UploadEvent(
    string UploadId,
    string Event,
    string Status,
    int? PartNumber = null,
    int? ReceivedParts = null,
    int? TotalParts = null,
    long? BytesReceived = null,
    string? FileName = null,
    string? VaultSlug = null,
    string? ErrorCode = null);

/// <summary>Broadcasts upload events to connected dashboards.</summary>
public interface IProgressPublisher
{
    Task PartConfirmedAsync(UploadEvent uploadEvent, CancellationToken cancellationToken);

    Task StatusChangedAsync(UploadEvent uploadEvent, CancellationToken cancellationToken);
}

/// <summary>No-op publisher used when no dashboard transport is registered.</summary>
public sealed class NullProgressPublisher : IProgressPublisher
{
    public Task PartConfirmedAsync(UploadEvent uploadEvent, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StatusChangedAsync(UploadEvent uploadEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}
