using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>Shared JSON settings. Property names are pinned explicitly on every DTO so the wire shape
/// matches PROTOCOL §2 exactly; nulls are always emitted (the manifest needs <c>"fileSha256": null</c>).</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
