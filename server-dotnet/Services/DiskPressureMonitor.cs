using AuroraFileBucket.Server.Configuration;
using Microsoft.Extensions.Options;

namespace AuroraFileBucket.Server.Services;

/// <summary>
/// Disk usage guard. When the volume that holds staging or the vaults goes above
/// <c>AuroraStorage:BackpressureDiskUsagePercent</c> the receiver answers 429 with a
/// <c>Retry-After</c> header.
/// </summary>
public sealed class DiskPressureMonitor
{
    private const int CacheMilliseconds = 1000;
    private const int DefaultRetryAfterSeconds = 30;

    private readonly StorageLayout _layout;
    private readonly AuroraStorageOptions _options;
    private readonly ILogger<DiskPressureMonitor> _logger;
    private readonly object _gate = new();

    private DateTimeOffset _sampledAt;
    private bool _underPressure;
    private int _usagePercent;

    public DiskPressureMonitor(
        StorageLayout layout,
        IOptions<AuroraStorageOptions> options,
        ILogger<DiskPressureMonitor> logger)
    {
        _layout = layout;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>True when the receiver must reject new writes with 429.</summary>
    public bool IsUnderPressure()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _sampledAt < TimeSpan.FromMilliseconds(CacheMilliseconds))
            {
                return _underPressure;
            }

            _sampledAt = now;
            _usagePercent = MeasureUsagePercent();
            _underPressure = _usagePercent >= 0 && _usagePercent >= ThresholdPercent;

            if (_underPressure)
            {
                _logger.LogWarning(
                    "Disk usage is {UsagePercent}% which is at or above the backpressure threshold of {Threshold}%; new writes are rejected with 429.",
                    _usagePercent,
                    ThresholdPercent);
            }

            return _underPressure;
        }
    }

    /// <summary>Configured threshold in percent.</summary>
    public int ThresholdPercent => _options.BackpressureDiskUsagePercent is > 0 and <= 100
        ? _options.BackpressureDiskUsagePercent
        : 85;

    /// <summary>Seconds the client must wait before retrying.</summary>
    public int RetryAfterSeconds => DefaultRetryAfterSeconds;

    private int MeasureUsagePercent()
    {
        var percent = -1;
        foreach (var root in new[] { _layout.StagingRoot, _layout.VaultsRoot })
        {
            var drive = DriveFor(root);
            if (drive is null)
            {
                continue;
            }

            try
            {
                var total = drive.TotalSize;
                if (total <= 0)
                {
                    continue;
                }

                var used = total - drive.AvailableFreeSpace;
                var value = (int)((used * 100L) / total);
                if (value > percent)
                {
                    percent = value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read the disk usage of {Root}.", root);
            }
        }

        return percent;
    }

    private static DriveInfo? DriveFor(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            return new DriveInfo(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
