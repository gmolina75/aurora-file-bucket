namespace AuroraFileBucket.Client.Transfer;

/// <summary>
/// Compression decisions of PROTOCOL §4.3.
/// <list type="bullet">
/// <item>Adaptive cutoff: a chunk whose compressed size exceeds 95 % of the raw size is sent
/// uncompressed (<c>X-Compression: none</c>).</item>
/// <item>Compressibility probe: the first 2 MB are compressed before the transfer starts; when the
/// reduction is below 8 % compression is disabled for the whole session.</item>
/// </list>
/// </summary>
public static class CompressionPolicy
{
    /// <summary>Per chunk cutoff of PROTOCOL §4.3: compressed size must not exceed 95 % of the raw size.</summary>
    public const double AdaptiveCutoffRatio = 0.95;

    /// <summary>
    /// Maximum compressed/raw ratio accepted by the compressibility probe. 0.92 is exactly the
    /// complement of the 8 % reduction mandated by PROTOCOL §4.3.
    /// </summary>
    public const double DefaultCompressibilityThreshold = 0.92;

    /// <summary>Number of bytes probed before the transfer starts.</summary>
    public const int ProbeSizeBytes = 2 * 1024 * 1024;

    /// <summary>True when the chunk must be sent uncompressed because compression did not pay off.</summary>
    public static bool ShouldSendUncompressed(long rawSize, long compressedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawSize);
        ArgumentOutOfRangeException.ThrowIfNegative(compressedSize);

        // Integer arithmetic to keep the 95 % boundary exact: compressed * 100 > raw * 95.
        return compressedSize * 100 > rawSize * 95;
    }

    /// <summary>Inverse of <see cref="ShouldSendUncompressed"/>.</summary>
    public static bool ShouldSendCompressed(long rawSize, long compressedSize)
        => !ShouldSendUncompressed(rawSize, compressedSize);

    /// <summary>
    /// True when compression is worth using for the whole session, i.e. the probe compressed the
    /// sample to at most <paramref name="threshold"/> of its original size (a reduction of 8 % or
    /// more with the default threshold).
    /// </summary>
    public static bool IsSessionCompressionWorthwhile(
        long rawSize,
        long compressedSize,
        double threshold = DefaultCompressibilityThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawSize);
        ArgumentOutOfRangeException.ThrowIfNegative(compressedSize);

        if (threshold <= 0)
        {
            return false;
        }

        return compressedSize <= rawSize * threshold;
    }

    /// <summary>Reduction obtained by the probe, as a ratio in [0, 1].</summary>
    public static double ReductionRatio(long rawSize, long compressedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawSize);
        ArgumentOutOfRangeException.ThrowIfNegative(compressedSize);
        return rawSize == 0 ? 0 : (double)(rawSize - compressedSize) / rawSize;
    }
}
