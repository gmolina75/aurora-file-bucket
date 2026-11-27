namespace AuroraFileBucket.Client.Protocol;

/// <summary>Abstraction over the sleep calls used by the retry policy, so unit tests never wait.</summary>
public interface IDelayProvider
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemDelayProvider : IDelayProvider
{
    public static readonly SystemDelayProvider Instance = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}
