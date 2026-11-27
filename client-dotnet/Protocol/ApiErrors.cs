using System.Text.Json;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>Parses the normative error envelope of PROTOCOL §0.</summary>
public static class ApiErrors
{
    public static async Task<ApiException> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            body = string.Empty;
        }

        string? code = null;
        JsonElement? details = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(body, ApiJson.Options);
            if (envelope?.Error is { } error)
            {
                code = error.Code;
                details = error.Details;
            }
        }
        catch (JsonException)
        {
            code = null;
        }

        var message = string.IsNullOrWhiteSpace(body)
            ? $"HTTP {(int)response.StatusCode} ({response.StatusCode})."
            : body;

        return new ApiException(response.StatusCode, code, message, details);
    }
}
