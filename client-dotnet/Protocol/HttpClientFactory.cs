namespace AuroraFileBucket.Client.Protocol;

/// <summary>Builds the <see cref="HttpClient"/> used by the sender.</summary>
public static class HttpClientFactory
{
    /// <summary>
    /// Creates a client with a generous timeout: a 32 MiB chunk at the slowest allowed bandwidth
    /// (1 Mbps) still needs more than four minutes.
    /// </summary>
    public static HttpClient Create(int concurrency, HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(2, concurrency + 2),
        })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        return client;
    }
}
