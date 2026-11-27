using System.Threading.Channels;

namespace AuroraFileBucket.Server.Services;

/// <summary>One queued assembly job.</summary>
public sealed record AssemblyJob(string UploadId, bool Notify);

/// <summary>
/// Queue of pending assemblies. <c>POST /complete</c> only enqueues, so the request
/// returns 202 immediately and the heavy work happens in the background worker.
/// </summary>
public sealed class AssemblyQueue
{
    private readonly Channel<AssemblyJob> _channel = Channel.CreateUnbounded<AssemblyJob>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public int PendingCount => _channel.Reader.Count;

    public bool TryEnqueue(AssemblyJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<AssemblyJob> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
