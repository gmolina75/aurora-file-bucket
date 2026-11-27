using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AuroraFileBucket.Client.Transfer;

/// <summary>Row of the <c>upload_progress</c> table (PROTOCOL §4.1).</summary>
public sealed class UploadProgressRow
{
    public string FilePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    /// <summary>Modification time of the file as Unix epoch seconds.</summary>
    public double FileMtime { get; set; }

    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Requested vault slug; empty when the receiver picks the default vault.</summary>
    public string VaultSlug { get; set; } = string.Empty;

    public string UploadId { get; set; } = string.Empty;

    public int ChunkSize { get; set; }

    public int TotalParts { get; set; }

    public string? FileSha256 { get; set; }

    public string State { get; set; } = UploadStates.Uploading;

    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>Values stored in <c>upload_progress.state</c>.</summary>
public static class UploadStates
{
    public const string Uploading = "UPLOADING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
}

/// <summary>
/// Local resume state. The schema is the one of PROTOCOL §4.1 and is shared verbatim with the
/// Python client.
/// </summary>
public sealed class ResumeStateStore : IDisposable
{
    private const string CreateProgressTable = """
        CREATE TABLE IF NOT EXISTS upload_progress (
          file_path   TEXT NOT NULL,
          file_size   INTEGER NOT NULL,
          file_mtime  REAL NOT NULL,
          server_url  TEXT NOT NULL,
          vault_slug  TEXT NOT NULL,
          upload_id   TEXT NOT NULL,
          chunk_size  INTEGER NOT NULL,
          total_parts INTEGER NOT NULL,
          file_sha256 TEXT,
          state       TEXT NOT NULL,
          updated_at  TEXT NOT NULL,
          PRIMARY KEY (file_path, file_size, server_url, vault_slug)
        );
        """;

    private const string CreatePartTable = """
        CREATE TABLE IF NOT EXISTS part_state (
          upload_id      TEXT NOT NULL,
          part_number    INTEGER NOT NULL,
          compression    TEXT,
          compressed_size INTEGER,
          raw_size       INTEGER,
          sha256         TEXT,
          uploaded_at    TEXT NOT NULL,
          PRIMARY KEY (upload_id, part_number)
        );
        """;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ResumeStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = fullPath;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = CreateProgressTable + ";" + CreatePartTable;
        command.ExecuteNonQuery();
    }

    public string DatabasePath { get; }

    /// <summary>RFC 3339 UTC timestamp used by the <c>*_at</c> columns.</summary>
    public static string Timestamp(DateTimeOffset moment)
        => moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Looks up the session registered for this file, server and vault (PROTOCOL §4.2 step 1).
    /// </summary>
    public async Task<UploadProgressRow?> FindProgressAsync(
        string filePath,
        long fileSize,
        string serverUrl,
        string vaultSlug,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT file_path, file_size, file_mtime, server_url, vault_slug, upload_id,
                       chunk_size, total_parts, file_sha256, state, updated_at
                  FROM upload_progress
                 WHERE file_path = $file_path
                   AND file_size = $file_size
                   AND server_url = $server_url
                   AND vault_slug = $vault_slug;
                """;
            command.Parameters.AddWithValue("$file_path", filePath);
            command.Parameters.AddWithValue("$file_size", fileSize);
            command.Parameters.AddWithValue("$server_url", serverUrl);
            command.Parameters.AddWithValue("$vault_slug", vaultSlug);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new UploadProgressRow
            {
                FilePath = reader.GetString(0),
                FileSize = reader.GetInt64(1),
                FileMtime = reader.GetDouble(2),
                ServerUrl = reader.GetString(3),
                VaultSlug = reader.GetString(4),
                UploadId = reader.GetString(5),
                ChunkSize = reader.GetInt32(6),
                TotalParts = reader.GetInt32(7),
                FileSha256 = reader.IsDBNull(8) ? null : reader.GetString(8),
                State = reader.GetString(9),
                UpdatedAt = reader.GetString(10),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Inserts or updates the row describing the current session.</summary>
    public async Task SaveProgressAsync(UploadProgressRow row, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO upload_progress
                    (file_path, file_size, file_mtime, server_url, vault_slug, upload_id,
                     chunk_size, total_parts, file_sha256, state, updated_at)
                VALUES
                    ($file_path, $file_size, $file_mtime, $server_url, $vault_slug, $upload_id,
                     $chunk_size, $total_parts, $file_sha256, $state, $updated_at)
                ON CONFLICT (file_path, file_size, server_url, vault_slug) DO UPDATE SET
                    file_mtime  = excluded.file_mtime,
                    upload_id   = excluded.upload_id,
                    chunk_size  = excluded.chunk_size,
                    total_parts = excluded.total_parts,
                    file_sha256 = excluded.file_sha256,
                    state       = excluded.state,
                    updated_at  = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$file_path", row.FilePath);
            command.Parameters.AddWithValue("$file_size", row.FileSize);
            command.Parameters.AddWithValue("$file_mtime", row.FileMtime);
            command.Parameters.AddWithValue("$server_url", row.ServerUrl);
            command.Parameters.AddWithValue("$vault_slug", row.VaultSlug);
            command.Parameters.AddWithValue("$upload_id", row.UploadId);
            command.Parameters.AddWithValue("$chunk_size", row.ChunkSize);
            command.Parameters.AddWithValue("$total_parts", row.TotalParts);
            command.Parameters.AddWithValue("$file_sha256", (object?)row.FileSha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", row.State);
            command.Parameters.AddWithValue("$updated_at", row.UpdatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records a confirmed part (PROTOCOL §4.1, <c>part_state</c>).</summary>
    public async Task SavePartAsync(
        string uploadId,
        int partNumber,
        string compression,
        long compressedSize,
        long rawSize,
        string sha256,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO part_state
                    (upload_id, part_number, compression, compressed_size, raw_size, sha256, uploaded_at)
                VALUES
                    ($upload_id, $part_number, $compression, $compressed_size, $raw_size, $sha256, $uploaded_at)
                ON CONFLICT (upload_id, part_number) DO UPDATE SET
                    compression     = excluded.compression,
                    compressed_size = excluded.compressed_size,
                    raw_size        = excluded.raw_size,
                    sha256          = excluded.sha256,
                    uploaded_at     = excluded.uploaded_at;
                """;
            command.Parameters.AddWithValue("$upload_id", uploadId);
            command.Parameters.AddWithValue("$part_number", partNumber);
            command.Parameters.AddWithValue("$compression", compression);
            command.Parameters.AddWithValue("$compressed_size", compressedSize);
            command.Parameters.AddWithValue("$raw_size", rawSize);
            command.Parameters.AddWithValue("$sha256", sha256);
            command.Parameters.AddWithValue("$uploaded_at", Timestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Updates the state column of a session.</summary>
    public async Task SetStateAsync(string uploadId, string state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE upload_progress
                   SET state = $state, updated_at = $updated_at
                 WHERE upload_id = $upload_id;
                """;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$updated_at", Timestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$upload_id", uploadId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
        _gate.Dispose();
    }
}
