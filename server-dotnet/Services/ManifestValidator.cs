using System.Globalization;

namespace AuroraFileBucket.Server.Services;

/// <summary>Validation of the manifest fields of the protocol.</summary>
public static class ManifestValidator
{
    public const int MaxFileNameLength = 255;

    /// <summary>Lowercase SHA-256 hex.</summary>
    public static bool IsSha256Hex(string? value)
        => value is not null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    /// <summary>SHA-256 hex in any case (headers are compared case insensitively).</summary>
    public static bool IsSha256HexAnyCase(string? value)
        => value is not null && value.Length == 64 && value.All(c => char.IsAsciiHexDigit(c));

    /// <summary>
    /// A file name is a plain base name: no directory separators, no reserved characters
    /// and no trailing dot or space (Windows rules).
    /// </summary>
    public static bool IsValidFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > MaxFileNameLength)
        {
            return false;
        }

        if (fileName is "." or "..")
        {
            return false;
        }

        if (fileName.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
        {
            return false;
        }

        if (fileName.Any(char.IsControl))
        {
            return false;
        }

        if (fileName.EndsWith('.') || fileName.EndsWith(' '))
        {
            return false;
        }

        return true;
    }

    /// <summary>Number of parts implied by the total size and the chunk size.</summary>
    public static int ExpectedPartCount(long totalBytes, long chunkSize)
        => (int)((totalBytes + chunkSize - 1) / chunkSize);

    /// <summary>Parses a mandatory integer header.</summary>
    public static bool TryParseHeaderInt(string? raw, out long value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
