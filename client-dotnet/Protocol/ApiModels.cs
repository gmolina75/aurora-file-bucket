using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuroraFileBucket.Client.Protocol;

/// <summary>Token pair returned by <c>POST /v1/auth/login</c> and <c>POST /v1/auth/refresh</c> (PROTOCOL §1.2).</summary>
public sealed class TokenPair
{
    [JsonPropertyName("tokenType")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("user")]
    public TokenUser? User { get; set; }

    [JsonPropertyName("vaults")]
    public List<string> Vaults { get; set; } = new();
}

public sealed class TokenUser
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

public sealed class LoginRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public sealed class RefreshRequest
{
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>Manifest registered by <c>POST /v1/uploads/init</c> (PROTOCOL §2.1).</summary>
public sealed class InitRequest
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; }

    [JsonPropertyName("totalParts")]
    public int TotalParts { get; set; }

    /// <summary>Always <c>null</c> on init: the global digest is computed incrementally and sent to /complete.</summary>
    [JsonPropertyName("fileSha256")]
    public string? FileSha256 { get; set; }

    /// <summary><c>auto</c> or <c>none</c>.</summary>
    [JsonPropertyName("compression")]
    public string Compression { get; set; } = "auto";
}

public sealed class UploadSession
{
    [JsonPropertyName("uploadId")]
    public string UploadId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("vaultSlug")]
    public string? VaultSlug { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; }

    [JsonPropertyName("totalParts")]
    public int TotalParts { get; set; }

    [JsonPropertyName("receivedParts")]
    public List<int> ReceivedParts { get; set; } = new();

    [JsonPropertyName("missingParts")]
    public List<int> MissingParts { get; set; } = new();

    [JsonPropertyName("bytesReceived")]
    public long BytesReceived { get; set; }

    [JsonPropertyName("startedAt")]
    public string? StartedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("completedAt")]
    public string? CompletedAt { get; set; }

    [JsonPropertyName("vaultPath")]
    public string? VaultPath { get; set; }

    [JsonPropertyName("finalSha256")]
    public string? FinalSha256 { get; set; }

    [JsonPropertyName("error")]
    public ApiErrorBody? Error { get; set; }
}

public sealed class PartReceipt
{
    [JsonPropertyName("partNumber")]
    public int PartNumber { get; set; }

    [JsonPropertyName("compressedSize")]
    public long CompressedSize { get; set; }

    [JsonPropertyName("rawSize")]
    public long RawSize { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("compression")]
    public string Compression { get; set; } = string.Empty;

    [JsonPropertyName("receivedParts")]
    public int ReceivedParts { get; set; }

    [JsonPropertyName("totalParts")]
    public int TotalParts { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public sealed class CompleteRequest
{
    [JsonPropertyName("fileSha256")]
    public string? FileSha256 { get; set; }

    [JsonPropertyName("notify")]
    public bool Notify { get; set; } = true;
}

public sealed class CompleteResponse
{
    [JsonPropertyName("uploadId")]
    public string UploadId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("statusUrl")]
    public string? StatusUrl { get; set; }
}

public sealed class ApiErrorBody
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public JsonElement? Details { get; set; }
}

public sealed class ApiErrorEnvelope
{
    [JsonPropertyName("error")]
    public ApiErrorBody Error { get; set; } = new();
}
