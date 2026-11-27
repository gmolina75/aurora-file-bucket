using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AuroraFileBucket.Client.Transfer;

/// <summary>
/// Progress of the transfer: percentage, parts, instantaneous throughput and ETA. Human readable
/// lines are written to stdout, or one JSON object per line when <c>--json-progress</c> is used.
/// </summary>
public sealed class ProgressReporter
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);

    private readonly bool _json;
    private readonly TextWriter _writer;
    private readonly long _totalBytes;
    private readonly int _totalParts;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _sync = new();

    private long _bytesSent;
    private int _partsDone;
    private long _lastReportTimestamp;
    private double _instantRate;

    public ProgressReporter(bool json, long totalBytes, int totalParts, TextWriter? writer = null)
    {
        _json = json;
        _totalBytes = Math.Max(0, totalBytes);
        _totalParts = Math.Max(0, totalParts);
        _writer = writer ?? Console.Out;
        _lastReportTimestamp = Stopwatch.GetTimestamp();
    }

    public int PartsDone => Volatile.Read(ref _partsDone);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Registers a confirmed part.</summary>
    public void OnPartCompleted(long transferredBytes)
    {
        Interlocked.Add(ref _bytesSent, transferredBytes);
        Interlocked.Increment(ref _partsDone);
        Report(force: false);
    }

    /// <summary>Writes the current progress.</summary>
    public void Report(bool force)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            double elapsed = _stopwatch.Elapsed.TotalSeconds;
            long bytes = Interlocked.Read(ref _bytesSent);
            int parts = Volatile.Read(ref _partsDone);

            double rate = elapsed > 0 ? bytes / elapsed : 0;
            double instant = (now - _lastReportTimestamp) * (double)Stopwatch.Frequency;
            if (instant > 0)
            {
                double recent = bytes / Math.Max(elapsed, 0.001);
                _instantRate = recent;
            }

            if (!force && instant < MinInterval.TotalSeconds * Stopwatch.Frequency)
            {
                return;
            }

            _lastReportTimestamp = now;

            double percent = _totalBytes > 0 ? Math.Min(100.0, bytes * 100.0 / _totalBytes) : 0;
            double etaSeconds = rate > 0 ? Math.Max(0, (_totalBytes - bytes) / rate) : 0;

            if (_json)
            {
                _writer.WriteLine(JsonSerializer.Serialize(new
                {
                    @event = "progress",
                    percent = Math.Round(percent, 2),
                    partsDone = parts,
                    partsTotal = _totalParts,
                    bytesSent = bytes,
                    bytesTotal = _totalBytes,
                    instantMbps = Math.Round(_instantRate * 8.0 / 1_000_000.0, 3),
                    etaSeconds = Math.Round(etaSeconds, 1),
                    timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                }));
            }
            else
            {
                _writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{percent,6:F2}%] {parts}/{_totalParts} parts  {FormatBytes(bytes)}/{FormatBytes(_totalBytes)}  {rate * 8.0 / 1_000_000.0:F2} Mbps  ETA {FormatDuration(etaSeconds)}"));
            }

            _writer.Flush();
        }
    }

    /// <summary>Writes the final summary of the transfer.</summary>
    public void ReportSummary(IReadOnlyDictionary<string, object?> details)
    {
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        long bytes = BytesSent;
        double rate = elapsed > 0 ? bytes / elapsed : 0;

        if (_json)
        {
            var payload = new Dictionary<string, object?>(details)
            {
                ["event"] = "completed",
                ["bytesSent"] = bytes,
                ["bytesTotal"] = _totalBytes,
                ["partsDone"] = PartsDone,
                ["partsTotal"] = _totalParts,
                ["durationSeconds"] = Math.Round(elapsed, 2),
                ["averageMbps"] = Math.Round(rate * 8.0 / 1_000_000.0, 3),
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };

            _writer.WriteLine(JsonSerializer.Serialize(payload));
            _writer.Flush();
            return;
        }

        _writer.WriteLine();
        _writer.WriteLine("Transfer finished.");
        foreach (KeyValuePair<string, object?> entry in details)
        {
            if (entry.Value is null)
            {
                continue;
            }

            _writer.WriteLine($"  {entry.Key}: {entry.Value}");
        }

        _writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  transferred: {FormatBytes(bytes)} in {FormatDuration(elapsed)} ({rate * 8.0 / 1_000_000.0:F2} Mbps average)"));
        _writer.Flush();
    }

    /// <summary>Writes an informational line, in the selected format.</summary>
    public void WriteEvent(string eventName, IReadOnlyDictionary<string, object?> details)
    {
        if (_json)
        {
            var payload = new Dictionary<string, object?>(details)
            {
                ["event"] = eventName,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };

            _writer.WriteLine(JsonSerializer.Serialize(payload));
            _writer.Flush();
            return;
        }

        _writer.WriteLine(string.Join(" ", details.Select(pair => $"{pair.Key}={pair.Value}")));
        _writer.Flush();
    }

    internal static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:F2} {units[unit]}");
    }

    internal static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }
}
