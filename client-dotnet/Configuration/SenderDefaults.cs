using System.Globalization;
using System.Text.Json;

namespace AuroraFileBucket.Client.Configuration;

/// <summary>
/// Optional defaults read from the <c>Sender</c> section of <c>appsettings.json</c>. They are only
/// used when the matching flag is absent from the command line, so the CLI always wins.
/// </summary>
public sealed class SenderDefaults
{
    public int ChunkSizeMiB { get; init; } = 16;

    public int MaxConcurrency { get; init; } = 3;

    public int MaxBandwidthMbps { get; init; } = 50;

    /// <summary>
    /// Maximum compressed/raw ratio accepted by the compressibility probe. 0.92 corresponds to the
    /// 8 % reduction mandated by PROTOCOL §4.3.
    /// </summary>
    public double CompressibilityThreshold { get; init; } = 0.92;

    public string StateDbPath { get; init; } = "aurora_state.db";

    /// <summary>Path of the file the defaults were read from, when there was one.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Loads the defaults from <c>appsettings.json</c>, looking in the working directory first and
    /// then next to the executable. A missing or unreadable file is not an error: the built in
    /// defaults are used.
    /// </summary>
    public static SenderDefaults Load(string? explicitPath = null)
    {
        string? candidate = explicitPath;
        if (candidate is null)
        {
            foreach (string directory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                string probe = Path.Combine(directory, "appsettings.json");
                if (File.Exists(probe))
                {
                    candidate = probe;
                    break;
                }
            }
        }

        if (candidate is null || !File.Exists(candidate))
        {
            return new SenderDefaults();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(candidate));
            if (!document.RootElement.TryGetProperty("Sender", out JsonElement sender) ||
                sender.ValueKind != JsonValueKind.Object)
            {
                return new SenderDefaults();
            }

            return new SenderDefaults
            {
                ChunkSizeMiB = ReadInt(sender, "ChunkSizeMiB", 16),
                MaxConcurrency = ReadInt(sender, "MaxConcurrency", 3),
                MaxBandwidthMbps = ReadInt(sender, "MaxBandwidthMbps", 50),
                CompressibilityThreshold = ReadDouble(sender, "CompressibilityThreshold", 0.92),
                StateDbPath = ReadString(sender, "StateDbPath") ?? "aurora_state.db",
                SourcePath = candidate,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SenderDefaults();
        }
    }

    private static int ReadInt(JsonElement parent, string name, int fallback)
        => parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : fallback;

    private static double ReadDouble(JsonElement parent, string name, double fallback)
        => parent.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result)
            ? result
            : fallback;

    private static string? ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"chunk={ChunkSizeMiB}MiB concurrency={MaxConcurrency} bandwidth={MaxBandwidthMbps}Mbps state={StateDbPath}");
}
