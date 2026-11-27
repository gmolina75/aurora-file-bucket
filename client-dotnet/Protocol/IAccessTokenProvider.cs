namespace AuroraFileBucket.Client.Protocol;

/// <summary>Supplies the bearer token used on every request and knows how to renew it.</summary>
public interface IAccessTokenProvider
{
    string AccessToken { get; }

    /// <summary>True when a refresh token is available, i.e. the session can be renewed.</summary>
    bool CanRefresh { get; }

    /// <summary>
    /// Renews the access token when less than three minutes of its TTL are left (PROTOCOL §1.4).
    /// </summary>
    Task EnsureFreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forces a renewal. <paramref name="tokenUsed"/> is the token the caller used; when it is no
    /// longer the current one another worker already renewed the session and the call is a no-op.
    /// Returns false when the session cannot be renewed.
    /// </summary>
    Task<bool> RefreshAsync(string tokenUsed, CancellationToken cancellationToken);
}
