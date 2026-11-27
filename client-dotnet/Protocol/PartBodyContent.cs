using System.Net;
using System.Net.Http.Headers;
using TokenBucket = AuroraFileBucket.Client.Transfer.TokenBucket;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>
/// Body of a chunk upload. Writes the transferred bytes to the network in blocks, acquiring tokens
/// from the global token bucket before each block so the limiter throttles the real traffic and not
/// just the production of the bytes.
/// </summary>
public sealed class PartBodyContent : HttpContent
{
    private const int BlockSize = 64 * 1024;

    private readonly byte[] _data;
    private readonly int _offset;
    private readonly int _count;
    private readonly TokenBucket? _bucket;
    private readonly CancellationToken _cancellationToken;

    public PartBodyContent(
        byte[] data,
        int offset,
        int count,
        TokenBucket? bucket = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, data.Length);

        _data = data;
        _offset = offset;
        _count = count;
        _bucket = bucket;
        _cancellationToken = cancellationToken;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _count;
        return true;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        int written = 0;
        while (written < _count)
        {
            int block = Math.Min(BlockSize, _count - written);
            if (_bucket is not null)
            {
                await _bucket.AcquireAsync(block, _cancellationToken).ConfigureAwait(false);
            }

            await stream.WriteAsync(_data.AsMemory(_offset + written, block), _cancellationToken).ConfigureAwait(false);
            written += block;
        }
    }
}
