using System.Security.Cryptography;
using System.Text;
using AuroraFileBucket.Server.Configuration;

namespace AuroraFileBucket.Server.Services;

/// <summary>
/// HMAC-SHA256 signing material for the access tokens. When
/// <c>Auth:JwtSigningKey</c> is empty an ephemeral key is generated, which invalidates
/// every token on restart; that is logged as a warning.
/// </summary>
public sealed class SigningKey
{
    private SigningKey(byte[] key, bool ephemeral)
    {
        Key = key;
        IsEphemeral = ephemeral;
    }

    public byte[] Key { get; }

    public bool IsEphemeral { get; }

    public static SigningKey Resolve(AuthOptions options, ILogger logger)
    {
        var configured = options.JwtSigningKey ?? string.Empty;

        if (string.IsNullOrWhiteSpace(configured))
        {
            var generated = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning(
                "Auth:JwtSigningKey is empty. An ephemeral signing key was generated: every access token is invalidated on restart. Set Auth__JwtSigningKey in production.");
            return new SigningKey(generated, ephemeral: true);
        }

        var bytes = Encoding.UTF8.GetBytes(configured);
        if (bytes.Length < 32)
        {
            // HMAC-SHA256 needs at least 256 bits of key material.
            bytes = SHA256.HashData(bytes);
            logger.LogWarning(
                "Auth:JwtSigningKey is shorter than 32 bytes; it was stretched with SHA-256 to reach the 256 bit minimum for HS256.");
        }

        return new SigningKey(bytes, ephemeral: false);
    }
}
