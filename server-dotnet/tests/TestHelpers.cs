using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuroraFileBucket.Server.Contracts;

namespace AuroraFileBucket.Server.Tests;

/// <summary>Small helpers shared by the protocol tests.</summary>
internal static class TestHelpers
{
    public const int MiB = 1024 * 1024;

    public static string Sha256Hex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static byte[] Pattern(int size, int seed)
    {
        var buffer = new byte[size];
        for (var i = 0; i < size; i++)
        {
            buffer[i] = (byte)((i * 31 + seed) % 251);
        }

        return buffer;
    }

    public static string Sha256OfParts(IEnumerable<byte[]> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hash.AppendData(part);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Compresses with the same Zstandard implementation the clients use.</summary>
    public static byte[] ZstdCompress(byte[] raw, int level = 3)
    {
        using var compressor = new ZstdSharp.Compressor { Level = level };
        var buffer = new byte[ZstdSharp.Compressor.GetCompressBound(raw.Length)];
        var written = compressor.Wrap(raw, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    public static async Task<TokenPair> LoginAsync(
        HttpClient client,
        string email = AuroraAppFactory.DevEmail,
        string password = AuroraAppFactory.DevPassword)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPair>())!;
    }

    public static async Task<HttpResponseMessage> InitAsync(
        HttpClient client,
        string accessToken,
        InitRequest request,
        string? vaultSlug = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads/init")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (vaultSlug is not null)
        {
            message.Headers.Add("X-Vault-Slug", vaultSlug);
        }

        return await client.SendAsync(message);
    }

    public static async Task<HttpResponseMessage> PutPartAsync(
        HttpClient client,
        string accessToken,
        string uploadId,
        int partNumber,
        byte[] body,
        string compression = "none",
        string? rawSha256 = null,
        bool omitChunkSha256 = false,
        bool omitRawSize = false,
        bool omitContentLength = false)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/v1/uploads/{uploadId}/parts/{partNumber}")
        {
            Content = new ByteArrayContent(body)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        if (!omitChunkSha256)
        {
            message.Headers.Add("X-Chunk-Sha256", Sha256Hex(body));
        }

        if (!omitRawSize)
        {
            message.Headers.Add("X-Raw-Size", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        message.Headers.Add("X-Compression", compression);

        if (rawSha256 is not null)
        {
            message.Headers.Add("X-Raw-Sha256", rawSha256);
        }

        if (omitContentLength)
        {
            message.Headers.TransferEncodingChunked = true;
            message.Content.Headers.ContentLength = null;
        }

        return await client.SendAsync(message);
    }

    public static async Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        string accessToken,
        string uploadId,
        string? fileSha256 = null,
        bool notify = false)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/uploads/{uploadId}/complete")
        {
            Content = JsonContent.Create(new { fileSha256, notify })
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(message);
    }

    public static async Task<UploadSessionStatus> GetStatusAsync(HttpClient client, string accessToken, string uploadId)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"/v1/uploads/{uploadId}/status");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadSessionStatus>())!;
    }

    /// <summary>Reads the error code of an error envelope.</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("error", out var error))
        {
            return null;
        }

        return error.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Waits until the session reaches a final state.</summary>
    public static async Task<UploadSessionStatus> WaitForFinalStateAsync(
        HttpClient client,
        string accessToken,
        string uploadId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        UploadSessionStatus? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await GetStatusAsync(client, accessToken, uploadId);
            if (last.Status is "READY" or "FAILED" or "EXPIRED")
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"The upload {uploadId} did not reach a final state. Last status: {last?.Status}.");
    }

    public static string ReadAllText(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));
}
