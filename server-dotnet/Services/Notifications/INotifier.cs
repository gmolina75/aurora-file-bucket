namespace AuroraFileBucket.Server.Services.Notifications;

/// <summary>One notification to deliver by e-mail and/or Telegram.</summary>
public sealed record NotificationMessage(
    bool Success,
    string UploadId,
    string FileName,
    string VaultSlug,
    long TotalBytes,
    string? FinalSha256,
    string? VaultPath,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CompletedAt);

/// <summary>Delivery channel for upload notifications.</summary>
public interface INotifier
{
    /// <summary>Name of the channel, used for logging.</summary>
    string Channel { get; }

    /// <summary>True when the channel is enabled and completely configured.</summary>
    bool IsEnabled { get; }

    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
