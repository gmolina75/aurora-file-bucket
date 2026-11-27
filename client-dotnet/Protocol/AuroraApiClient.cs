using System.Net;
using System.Text;
using AuroraFileBucket.Client.Transfer;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>
/// Client of the Aurora File Bucket v1 API. Implements the endpoint paths, headers and status codes
/// of PROTOCOL §2 and the retry rules of §4.4.
/// </summary>
public sealed class AuroraApiClient
{
    /// <summary>Value of <c>X-Client-Platform</c> for this implementation (PROTOCOL §2.1).</summary>
    public const string ClientPlatform = "windows-dotnet";

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly IDelayProvider _delay;
    private readonly int _maxRetries;
    private readonly string _clientId;
    private readonly string _baseUrl;

    public AuroraApiClient(
        HttpClient http,
        IAccessTokenProvider tokens,
        IDelayProvider delay,
        int maxRetries,
        string clientId,
        string baseUrl)
    {
        _http = http;
        _tokens = tokens;
        _delay = delay;
        _maxRetries = Math.Max(0, maxRetries);
        _clientId = clientId;
        _baseUrl = baseUrl;
    }

    public string ClientId => _clientId;

    /// <summary>Normalises a user supplied server URL into the API base (<c>https://host/v1</c>).</summary>
    public static string NormalizeBaseUrl(string serverUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        var trimmed = serverUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/v1";
    }

    public async Task<UploadSession> InitAsync(
        InitRequest request,
        string? vaultSlug,
        CancellationToken cancellationToken)
    {
        string json = ApiJson.Serialize(request);
        using var response = await SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, BuildUri("uploads/init"));
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(vaultSlug))
                {
                    message.Headers.TryAddWithoutValidation("X-Vault-Slug", vaultSlug);
                }

                return Prepare(message);
            },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<UploadSession>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uploads one part. The body is the transferred bytes (compressed when zstd was used).</summary>
    public async Task<PartReceipt> PutPartAsync(
        string uploadId,
        int partNumber,
        byte[] body,
        int offset,
        int count,
        string chunkSha256,
        string rawSha256,
        string compression,
        long rawSize,
        TokenBucket? bucket,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(
                    HttpMethod.Put,
                    BuildUri($"uploads/{Uri.EscapeDataString(uploadId)}/parts/{partNumber}"));

                message.Content = new PartBodyContent(body, offset, count, bucket, cancellationToken);
                message.Headers.TryAddWithoutValidation("X-Chunk-Sha256", chunkSha256);
                message.Headers.TryAddWithoutValidation("X-Raw-Sha256", rawSha256);
                message.Headers.TryAddWithoutValidation("X-Compression", compression);
                message.Headers.TryAddWithoutValidation("X-Raw-Size", rawSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Prepare(message);
            },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<PartReceipt>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UploadSession> GetStatusAsync(string uploadId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => Prepare(new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri($"uploads/{Uri.EscapeDataString(uploadId)}/status"))),
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<UploadSession>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompleteResponse> CompleteAsync(
        string uploadId,
        string? fileSha256,
        bool notify,
        CancellationToken cancellationToken)
    {
        string json = ApiJson.Serialize(new CompleteRequest { FileSha256 = fileSha256, Notify = notify });
        using var response = await SendAsync(
            () =>
            {
                var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    BuildUri($"uploads/{Uri.EscapeDataString(uploadId)}/complete"));
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return Prepare(message);
            },
            cancellationToken).ConfigureAwait(false);

        return await ReadAsync<CompleteResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private Uri BuildUri(string relativePath) => new(_baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/'));

    /// <summary>Adds the common headers of PROTOCOL §5 to a request.</summary>
    private HttpRequestMessage Prepare(HttpRequestMessage message)
    {
        string token = _tokens.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        message.Headers.TryAddWithoutValidation("X-Client-Id", _clientId);
        message.Headers.TryAddWithoutValidation("X-Client-Platform", ClientPlatform);
        return message;
    }

    /// <summary>
    /// Sends a request applying the retry rules of PROTOCOL §4.4: 429 honours <c>Retry-After</c>,
    /// 5xx/network errors use the exponential backoff, and a 401 triggers exactly one token renewal.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        bool refreshAttempted = false;

        while (true)
        {
            attempt++;
            await _tokens.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                using var request = requestFactory();
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt > _maxRetries)
                {
                    throw new ApiException(HttpStatusCode.ServiceUnavailable, "network_error", ex.Message);
                }

                await _delay.DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt > _maxRetries)
                {
                    throw new ApiException(HttpStatusCode.RequestTimeout, "timeout", ex.Message);
                }

                await _delay.DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshAttempted)
            {
                string tokenUsed = _tokens.AccessToken;
                if (await _tokens.RefreshAsync(tokenUsed, cancellationToken).ConfigureAwait(false))
                {
                    refreshAttempted = true;
                    response.Dispose();
                    continue;
                }
            }

            if (RetryPolicy.IsRetryable(response.StatusCode))
            {
                if (attempt > _maxRetries)
                {
                    return response;
                }

                var delay = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? RetryPolicy.RetryAfterDelay(response, Backoff(attempt))
                    : Backoff(attempt);

                response.Dispose();
                await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private TimeSpan Backoff(int attempt) => RetryPolicy.BackoffDelay(attempt, Random.Shared.NextDouble());

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrors.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = ApiJson.Deserialize<T>(body);
        if (parsed is null)
        {
            throw new ApiException(response.StatusCode, "invalid_response", "The receiver returned an empty payload.");
        }

        return parsed;
    }
}
