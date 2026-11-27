using System.Net;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>
/// Retry rules of PROTOCOL §4.4.
/// <list type="bullet">
/// <item><c>429</c>: wait exactly <c>Retry-After</c> seconds and retry.</item>
/// <item><c>5xx</c>, network error, timeout: exponential backoff with jitter (1 s, 2 s, 4 s, 8 s, 16 s).</item>
/// <item><c>400 chunk_hash_mismatch</c>: never retried, the transfer is aborted.</item>
/// <item><c>401</c>: the token is renewed once and the request is replayed.</item>
/// <item><c>409</c>: never retried, the transfer is aborted.</item>
/// </list>
/// </summary>
public static class RetryPolicy
{
    /// <summary>Base delays of the exponential backoff, in seconds.</summary>
    public static readonly TimeSpan[] BackoffLadder =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
    };

    /// <summary>True when the status code is safe to replay (429 and 5xx).</summary>
    public static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    /// <summary>True when the failure is a data error that must abort the transfer immediately.</summary>
    public static bool IsFatal(HttpStatusCode statusCode, string? errorCode)
    {
        if (statusCode == HttpStatusCode.Conflict)
        {
            return true;
        }

        if (statusCode == HttpStatusCode.BadRequest)
        {
            return true;
        }

        _ = errorCode;
        return false;
    }

    /// <summary>
    /// Exponential backoff with jitter. <paramref name="retryIndex"/> is 1-based; the base delay is
    /// 1 s, 2 s, 4 s, 8 s, 16 s. <paramref name="jitterFactor"/> must be in [0, 1) and adds up to
    /// 50 % on top of the base delay.
    /// </summary>
    public static TimeSpan BackoffDelay(int retryIndex, double jitterFactor)
    {
        var index = Math.Clamp(retryIndex, 1, BackoffLadder.Length) - 1;
        double baseSeconds = BackoffLadder[index].TotalSeconds;
        double jitter = Math.Clamp(jitterFactor, 0.0, 1.0) * baseSeconds * 0.5;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }

    /// <summary>
    /// Delay to honour for a 429: the exact <c>Retry-After</c> value when present, otherwise the
    /// regular backoff.
    /// </summary>
    public static TimeSpan RetryAfterDelay(HttpResponseMessage response, TimeSpan fallback)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return fallback;
    }
}
