namespace AuroraFileBucket.Server.Services.Notifications;

/// <summary>Notifier used when no delivery channel is configured.</summary>
public sealed class NullNotifier : INotifier
{
    public string Channel => "none";

    public bool IsEnabled => false;

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}
