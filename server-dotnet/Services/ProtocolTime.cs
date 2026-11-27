using System.Globalization;

namespace AuroraFileBucket.Server.Services;

/// <summary>
/// RFC 3339 UTC timestamps as required by the protocol
/// (<c>2026-11-27T14:03:11Z</c>).
/// </summary>
public static class ProtocolTime
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string ToProtocol(DateTimeOffset value)
        => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string value)
        => DateTimeOffset.ParseExact(
            value,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
