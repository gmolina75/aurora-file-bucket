using System.Buffers;
using ZstdSharp;

namespace AuroraFileBucket.Client.Transfer;

/// <summary>
/// Zstandard helpers: level 3 compression into pooled buffers, using the managed port
/// <c>ZstdSharp.Port</c>.
/// </summary>
public static class CompressionCodec
{
    /// <summary>Compression level mandated by PROTOCOL §4.3.</summary>
    public const int Level = 3;

    /// <summary>
    /// Compresses <paramref name="length"/> bytes starting at <paramref name="offset"/> into a pooled
    /// buffer. The caller owns the returned buffer and must return it to
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="compressor">
    /// Compressor to reuse. When null a private one is created and disposed, which is expensive
    /// enough to be worth avoiding in a loop.
    /// </param>
    public static byte[] Compress(
        byte[] source,
        int offset,
        int length,
        out int compressedLength,
        Compressor? compressor = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, source.Length);

        bool ownsCompressor = compressor is null;
        Compressor zstd = compressor ?? new Compressor(Level);

        try
        {
            int capacity = Compressor.GetCompressBound(length);
            byte[] destination = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));

            try
            {
                compressedLength = zstd.Wrap(source, offset, length, destination, 0, destination.Length);
                return destination;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(destination);
                throw;
            }
        }
        finally
        {
            if (ownsCompressor)
            {
                zstd.Dispose();
            }
        }
    }

    /// <summary>
    /// Compressibility probe: compresses the first <paramref name="sampleLength"/> bytes of the file
    /// and reports whether compression is worth using for the whole session.
    /// </summary>
    public static async Task<bool> ProbeAsync(
        string filePath,
        double threshold,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        int sampleLength = (int)Math.Min(CompressionPolicy.ProbeSizeBytes, info.Length);
        if (sampleLength <= 0)
        {
            return false;
        }

        byte[] sample = ArrayPool<byte>.Shared.Rent(sampleLength);
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            int read = 0;
            while (read < sampleLength)
            {
                int n = await stream.ReadAsync(sample.AsMemory(read, sampleLength - read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read == 0)
            {
                return false;
            }

            using var compressor = new Compressor(Level);
            byte[] compressed = Compress(sample, 0, read, out int compressedLength, compressor);
            try
            {
                return CompressionPolicy.IsSessionCompressionWorthwhile(read, compressedLength, threshold);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sample);
        }
    }

    /// <summary>Lowercase hexadecimal SHA-256 of a buffer region.</summary>
    public static string Sha256Hex(byte[] buffer, int offset, int count)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(offset, count));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
