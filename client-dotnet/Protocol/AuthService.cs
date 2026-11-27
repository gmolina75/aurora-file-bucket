using System.Text;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>
/// Owns the access/refresh token pair: initial login, proactive renewal (PROTOCOL §1.4) and the
/// single renewal allowed after a 401 (PROTOCOL §4.4).
/// </summary>
public sealed class AuthService : IAccessTokenProvider
{
    /// <summary>Renew when less than this many seconds of the access token TTL are left.</summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IDelayProvider _delay;
    private readonly object _sync = new();

    private string _accessToken = string.Empty;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private Task<bool>? _refreshInFlight;

    public AuthService(HttpClient http, string baseUrl, IDelayProvider delay)
    {
        _http = http;
        _baseUrl = baseUrl;
        _delay = delay;
    }

    public string AccessToken => _accessToken;

    public bool CanRefresh => !string.IsNullOrEmpty(_refreshToken);

    /// <summary>Vaults the token is allowed to write to, as reported by the receiver.</summary>
    public IReadOnlyList<string> Vaults { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Authenticates with the credentials, or adopts an existing access token when one was supplied
    /// on the command line.
    /// </summary>
    public async Task AuthenticateAsync(
        string? email,
        string? password,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _accessToken = accessToken;
            _expiresAt = DateTimeOffset.MaxValue;
            return;
        }

        var pair = await PostAsync<LoginRequest, TokenPair>(
            "auth/login",
            new LoginRequest { Email = email ?? string.Empty, Password = password ?? string.Empty },
            cancellationToken).ConfigureAwait(false);

        Apply(pair);
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (!CanRefresh)
        {
            return;
        }

        if (_expiresAt - DateTimeOffset.UtcNow > RenewalWindow)
        {
            return;
        }

        await RefreshAsync(_accessToken, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RefreshAsync(string tokenUsed, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!CanRefresh)
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(_accessToken, tokenUsed, StringComparison.Ordinal))
            {
                // Another worker already rotated the pair; the new token is valid, no need to burn the
                // single-use refresh token twice.
                return Task.FromResult(true);
            }

            if (_refreshInFlight is { } inFlight)
            {
                return inFlight;
            }

            _refreshInFlight = RefreshCoreAsync(cancellationToken);
            return _refreshInFlight;
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!CanRefresh)
            {
                return false;
            }

            var pair = await PostAsync<RefreshRequest, TokenPair>(
                "auth/refresh",
                new RefreshRequest { RefreshToken = _refreshToken! },
                cancellationToken).ConfigureAwait(false);

            Apply(pair);
            return true;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _refreshInFlight = null;
            }
        }
    }

    private void Apply(TokenPair pair)
    {
        _accessToken = pair.AccessToken;
        _refreshToken = pair.RefreshToken;
        _expiresAt = pair.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(pair.ExpiresIn)
            : DateTimeOffset.MaxValue;
        if (pair.Vaults is { Count: > 0 })
        {
            Vaults = pair.Vaults;
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string relativePath,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        string json = ApiJson.Serialize(payload);
        var uri = new Uri(_baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/'));
        const int maxAttempts = 3;

        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                try
                {
                    response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    await _delay.DelayAsync(RetryPolicy.BackoffDelay(attempt, Random.Shared.NextDouble()), cancellationToken).ConfigureAwait(false);
                    _ = ex;
                    continue;
                }
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var parsed = ApiJson.Deserialize<TResponse>(body);
                    if (parsed is null)
                    {
                        throw new ApiException(response.StatusCode, "invalid_response", "The receiver returned an empty payload.");
                    }

                    return parsed;
                }

                if (RetryPolicy.IsRetryable(response.StatusCode) && attempt < maxAttempts)
                {
                    await _delay.DelayAsync(RetryPolicy.BackoffDelay(attempt, Random.Shared.NextDouble()), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await ApiErrors.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
