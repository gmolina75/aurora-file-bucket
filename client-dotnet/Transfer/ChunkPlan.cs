namespace AuroraFileBucket.Client.Transfer;

/// <summary>Position of one part inside the original file.</summary>
public readonly record struct ChunkDescriptor(int PartNumber, long Offset, int Length)
{
    public long EndOffset => Offset + Length;
}

/// <summary>
/// Uniform chunking of PROTOCOL §4.3: <c>totalParts = ceil(totalBytes / chunkSize)</c>, every part has
/// the same size except the last one, which holds the remainder.
/// </summary>
public static class ChunkPlan
{
    public const int MinChunkSizeMiB = 4;
    public const int MaxChunkSizeMiB = 32;

    public static int TotalParts(long totalBytes, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        return (int)((totalBytes + chunkSize - 1) / chunkSize);
    }

    /// <summary>Describes the part <paramref name="partNumber"/> (1-based).</summary>
    public static ChunkDescriptor Describe(long totalBytes, int chunkSize, int partNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partNumber, TotalParts(totalBytes, chunkSize));

        long offset = (long)(partNumber - 1) * chunkSize;
        int length = (int)Math.Min(chunkSize, totalBytes - offset);
        return new ChunkDescriptor(partNumber, offset, length);
    }

    public static IEnumerable<ChunkDescriptor> DescribeAll(long totalBytes, int chunkSize)
    {
        int totalParts = TotalParts(totalBytes, chunkSize);
        for (int part = 1; part <= totalParts; part++)
        {
            yield return Describe(totalBytes, chunkSize, part);
        }
    }
}
