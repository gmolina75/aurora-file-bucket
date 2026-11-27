using System.Diagnostics;

namespace AuroraFileBucket.Client.Transfer;

/// <summary>
/// Global token bucket shared by every worker thread (PROTOCOL §4.3). The rate is
/// <c>Mbps * 1_000_000 / 8</c> bytes per second and the bucket capacity is one second of traffic.
/// The limiter throttles the bytes actually written to the network.
/// </summary>
public sealed class TokenBucket
{
    private readonly long _rateBytesPerSecond;
    private readonly long _capacityBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random;

    private double _available;
    private long _lastTimestamp;

    public TokenBucket(long rateBytesPerSecond, long? capacityBytes = null, double? initialTokens = null, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateBytesPerSecond);

        _rateBytesPerSecond = rateBytesPerSecond;
        _capacityBytes = capacityBytes ?? rateBytesPerSecond;
        _random = random ?? Random.Shared;
        _available = Math.Clamp(initialTokens ?? _capacityBytes, 0, _capacityBytes);
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Creates the bucket described by PROTOCOL §4.3 for a bandwidth expressed in Mbps.</summary>
    public static TokenBucket FromMbps(double megabitsPerSecond)
    {
        double bytesPerSecond = megabitsPerSecond * 1_000_000.0 / 8.0;
        return new TokenBucket((long)Math.Max(1, bytesPerSecond));
    }

    public long RateBytesPerSecond => _rateBytesPerSecond;

    public long CapacityBytes => _capacityBytes;

    /// <summary>Waits until <paramref name="byteCount"/> bytes may be transferred.</summary>
    public async ValueTask AcquireAsync(int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0)
        {
            return;
        }

        long remaining = byteCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long chunk;
            long waitTicks;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Refill();

                chunk = Math.Min(remaining, (long)Math.Floor(_available));
                _available -= chunk;
                remaining -= chunk;

                waitTicks = remaining > 0
                    ? (long)Math.Ceiling(remaining * (double)Stopwatch.Frequency / _rateBytesPerSecond)
                    : 0;
            }
            finally
            {
                _gate.Release();
            }

            if (remaining > 0)
            {
                TimeSpan wait = TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _lastTimestamp;
        if (elapsed <= 0)
        {
            return;
        }

        _lastTimestamp = now;
        double refilled = elapsed * (double)_rateBytesPerSecond / Stopwatch.Frequency;
        _available = Math.Min(_capacityBytes, _available + refilled);
    }
}
