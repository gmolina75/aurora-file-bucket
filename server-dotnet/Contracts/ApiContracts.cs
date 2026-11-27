using System.Text.Json.Serialization;

namespace AuroraFileBucket.Server.Contracts;

/// <summary>Error body of the protocol envelope: <c>{"error":{"code","message","details"?}}</c>.</summary>
public sealed record ErrorBody(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>Protocol error envelope.</summary>
public sealed record ErrorEnvelope(ErrorBody Error);

/// <summary>Login payload.</summary>
public sealed record LoginRequest(string? Email, string? Password);

/// <summary>Refresh payload.</summary>
public sealed record RefreshRequest(string? RefreshToken);

/// <summary>Upload manifest payload (POST /v1/uploads/init).</summary>
public sealed record InitRequest(
    string? FileName,
    long? TotalBytes,
    long? ChunkSize,
    int? TotalParts,
    string? FileSha256,
    string? Compression);

/// <summary>Assembly trigger payload (POST /v1/uploads/{id}/complete).</summary>
public sealed record CompleteRequest(string? FileSha256, bool? Notify);

/// <summary>Token pair returned by /v1/auth/login and /v1/auth/refresh.</summary>
public sealed record TokenPair(
    string TokenType,
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    UserInfo User,
    IReadOnlyList<string> Vaults);

public sealed record UserInfo(string Email, string? DisplayName);

/// <summary>201 response of POST /v1/uploads/init.</summary>
public sealed record UploadSessionCreated(
    string UploadId,
    string Status,
    string VaultSlug,
    string FileName,
    long TotalBytes,
    long ChunkSize,
    int TotalParts,
    IReadOnlyList<int> ReceivedParts,
    IReadOnlyList<int> MissingParts,
    string ExpiresAt);

/// <summary>200 response of GET /v1/uploads/{id}/status.</summary>
public sealed record UploadSessionStatus(
    string UploadId,
    string Status,
    string VaultSlug,
    string FileName,
    long TotalBytes,
    long ChunkSize,
    int TotalParts,
    IReadOnlyList<int> ReceivedParts,
    IReadOnlyList<int> MissingParts,
    long BytesReceived,
    string StartedAt,
    string ExpiresAt,
    string? CompletedAt,
    string? VaultPath,
    string? FinalSha256,
    ErrorBody? Error);

/// <summary>200 response of PUT /v1/uploads/{id}/parts/{n}.</summary>
public sealed record PartReceipt(
    int PartNumber,
    long CompressedSize,
    long RawSize,
    string Sha256,
    string Compression,
    int ReceivedParts,
    int TotalParts,
    string Status);

/// <summary>202 response of POST /v1/uploads/{id}/complete.</summary>
public sealed record AssemblyAccepted(string UploadId, string Status, string StatusUrl);

/// <summary>Admin listing entry (GET /v1/admin/uploads).</summary>
public sealed record AdminUploadRow(
    string UploadId,
    string Status,
    string VaultSlug,
    string FileName,
    long TotalBytes,
    long BytesReceived,
    int TotalParts,
    int ReceivedPartCount,
    string StartedAt,
    string? CompletedAt,
    string? FinalSha256,
    string? ErrorCode);
