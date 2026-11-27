using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Data.SQLite;
using Newtonsoft.Json;
using AuroraFileBucket.Legacy.Services;

namespace AuroraFileBucket.Legacy.Services
{
    public class UploadSession
    {
        public string UploadId { get; set; }
        public string VaultSlug { get; set; }
        public string FileName { get; set; }
        public long TotalBytes { get; set; }
        public long ChunkSize { get; set; }
        public int TotalParts { get; set; }
        public string ExpectedSha256 { get; set; }
        public string Status { get; set; }
        public string ClientId { get; set; }
        public string ClientPlatform { get; set; }
        public string VaultPath { get; set; }
        public string FinalSha256 { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public class PartReceipt
    {
        public int PartNumber { get; set; }
        public long CompressedSize { get; set; }
        public long RawSize { get; set; }
        public string Sha256 { get; set; }
        public string Compression { get; set; }
        public int ReceivedParts { get; set; }
        public int TotalParts { get; set; }
        public string Status { get; set; }
    }

    public class UploadStatus
    {
        public string UploadId { get; set; }
        public string Status { get; set; }
        public string VaultSlug { get; set; }
        public string FileName { get; set; }
        public long TotalBytes { get; set; }
        public long ChunkSize { get; set; }
        public int TotalParts { get; set; }
        public List<int> ReceivedParts { get; set; }
        public List<int> MissingParts { get; set; }
        public long BytesReceived { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string VaultPath { get; set; }
        public string FinalSha256 { get; set; }
        public string ErrorCode { get; set; }
    }

    public class StorageService
    {
        private readonly string _stagingRoot;
        private readonly string _vaultsRoot;
        private readonly long _maxChunkBytes;
        private readonly int _backpressurePercent;

        public StorageService()
        {
            _stagingRoot = ConfigurationManager.AppSettings["StagingRoot"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "Staging");
            _vaultsRoot = ConfigurationManager.AppSettings["VaultsRoot"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "Vaults");
            _maxChunkBytes = long.Parse(ConfigurationManager.AppSettings["MaxChunkBytes"] ?? "37748736");
            _backpressurePercent = int.Parse(ConfigurationManager.AppSettings["BackpressurePercent"] ?? "85");

            if (!Directory.Exists(_stagingRoot)) Directory.CreateDirectory(_stagingRoot);
            if (!Directory.Exists(_vaultsRoot)) Directory.CreateDirectory(_vaultsRoot);
        }

        public UploadSession InitUpload(string fileName, long totalBytes, long chunkSize, int totalParts, string expectedSha256, string vaultSlug, string clientId, string clientPlatform)
        {
            if (totalBytes <= 0 || chunkSize <= 0 || totalParts <= 0)
                throw new ArgumentException("Invalid manifest parameters");

            var expectedParts = (int)Math.Ceiling((double)totalBytes / chunkSize);
            if (totalParts != expectedParts)
                throw new ArgumentException($"totalParts must be {expectedParts} for {totalBytes} bytes with chunk size {chunkSize}");

            // Verificar bóveda
            var vaultPath = GetVaultPath(vaultSlug);
            if (vaultPath == null)
                throw new UnauthorizedAccessException($"Vault '{vaultSlug}' not found or not allowed");

            // Verificar cuota
            if (WouldExceedQuota(vaultSlug, totalBytes))
                throw new InvalidOperationException("Vault quota exceeded");

            // Verificar backpressure
            if (IsUnderPressure())
                throw new InvalidOperationException("Server storage under backpressure");

            string uploadId = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;
            var expiresAt = now.AddHours(72);

            // Crear carpeta de staging
            string stagingFolder = Path.Combine(_stagingRoot, uploadId);
            Directory.CreateDirectory(stagingFolder);

            using (var conn = DatabaseHelper.GetConnection())
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT INTO upload_sessions (
                            upload_id, vault_slug, file_name, total_bytes, chunk_size, total_parts,
                            expected_sha256, status, client_id, client_platform,
                            started_at, expires_at
                        ) VALUES (
                            @upload_id, @vault_slug, @file_name, @total_bytes, @chunk_size, @total_parts,
                            @expected_sha256, 'INIT', @client_id, @client_platform,
                            @started_at, @expires_at
                        )";

                    cmd.Parameters.AddWithValue("@upload_id", uploadId);
                    cmd.Parameters.AddWithValue("@vault_slug", vaultSlug);
                    cmd.Parameters.AddWithValue("@file_name", fileName);
                    cmd.Parameters.AddWithValue("@total_bytes", totalBytes);
                    cmd.Parameters.AddWithValue("@chunk_size", chunkSize);
                    cmd.Parameters.AddWithValue("@total_parts", totalParts);
                    cmd.Parameters.AddWithValue("@expected_sha256", expectedSha256 ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@client_id", clientId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@client_platform", clientPlatform ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@started_at", now.ToString("o"));
                    cmd.Parameters.AddWithValue("@expires_at", expiresAt.ToString("o"));
                    
                    cmd.ExecuteNonQuery();
                }
            }

            return new UploadSession
            {
                UploadId = uploadId,
                VaultSlug = vaultSlug,
                FileName = fileName,
                TotalBytes = totalBytes,
                ChunkSize = chunkSize,
                TotalParts = totalParts,
                ExpectedSha256 = expectedSha256,
                Status = "INIT",
                ClientId = clientId,
                ClientPlatform = clientPlatform,
                StartedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
            };
        }

        public PartReceipt PutPart(string uploadId, int partNumber, Stream content, string chunkSha256, string compression, long rawSize, string rawSha256)
        {
            var session = GetSession(uploadId);
            if (session == null)
                throw new FileNotFoundException("Upload session not found");

            if (session.Status != "INIT" && session.Status != "UPLOADING")
                throw new InvalidOperationException($"Upload is in state '{session.Status}' and does not accept more parts");

            if (partNumber < 1 || partNumber > session.TotalParts)
                throw new ArgumentException($"partNumber must be between 1 and {session.TotalParts}");

            // Verificar hash del chunk
            using (var sha256 = SHA256.Create())
            {
                // Nota: el stream ya fue leído, necesitamos el buffer
            }

            // Guardar en staging
            string stagingFolder = Path.Combine(_stagingRoot, uploadId);
            string partPath = Path.Combine(stagingFolder, $"part_{partNumber:D5}.bin");
            
            Directory.CreateDirectory(stagingFolder);

            // Leer contenido y calcular hash
            byte[] contentBytes;
            using (var ms = new MemoryStream())
            {
                content.CopyTo(ms);
                contentBytes = ms.ToArray();
            }

            string computedHash = ComputeSha256Hex(contentBytes);
            if (!string.Equals(computedHash, chunkSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(partPath)) File.Delete(partPath);
                throw new InvalidOperationException("Chunk hash mismatch");
            }

            // Verificar rawSize si se proporcionó
            if (rawSize != contentBytes.Length && compression == "none")
            {
                throw new ArgumentException("X-Raw-Size does not match content length");
            }

            // Idempotencia: verificar si ya existe con mismo hash
            var existingPart = GetPart(uploadId, partNumber);
            if (existingPart != null)
            {
                if (!string.Equals(existingPart.ChunkSha256, chunkSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Part {partNumber} already stored with different hash");
                }
                // Ya existe y es idéntico - OK
            }
            else
            {
                // Guardar archivo
                File.WriteAllBytes(partPath, contentBytes);
                
                // Guardar en BD
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            INSERT INTO upload_parts (
                                upload_id, part_number, raw_offset, raw_size,
                                compressed_size, compression, chunk_sha256, raw_sha256, confirmed_at
                            ) VALUES (
                                @upload_id, @part_number, @raw_offset, @raw_size,
                                @compressed_size, @compression, @chunk_sha256, @raw_sha256, @confirmed_at
                            )";

                    cmd.Parameters.AddWithValue("@upload_id", uploadId);
                    cmd.Parameters.AddWithValue("@part_number", partNumber);
                    cmd.Parameters.AddWithValue("@raw_offset", (long)(partNumber - 1) * session.ChunkSize);
                    cmd.Parameters.AddWithValue("@raw_size", rawSize);
                    cmd.Parameters.AddWithValue("@compressed_size", contentBytes.Length);
                    cmd.Parameters.AddWithValue("@compression", compression ?? "none");
                    cmd.Parameters.AddWithValue("@chunk_sha256", computedHash);
                    cmd.Parameters.AddWithValue("@raw_sha256", rawSha256 ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@confirmed_at", DateTime.UtcNow.ToString("o"));
                    
                    cmd.ExecuteNonQuery();
                }
            }

            // Actualizar estado de sesión
            if (session.Status == "INIT")
            {
                UpdateSessionStatus(uploadId, "UPLOADING");
                session.Status = "UPLOADING";
            }

            var receivedParts = GetReceivedParts(uploadId);
            
            return new PartReceipt
            {
                PartNumber = partNumber,
                CompressedSize = contentBytes.Length,
                RawSize = rawSize,
                Sha256 = computedHash,
                Compression = compression ?? "none",
                ReceivedParts = receivedParts.Count,
                TotalParts = session.TotalParts,
                Status = session.Status
            };
        }

        public UploadStatus GetStatus(string uploadId)
        {
            var session = GetSession(uploadId);
            if (session == null) return null;

            var receivedParts = GetReceivedParts(uploadId);
            var allParts = Enumerable.Range(1, session.TotalParts).ToList();
            var missingParts = allParts.Except(receivedParts).ToList();

            return new UploadStatus
            {
                UploadId = session.UploadId,
                Status = session.Status,
                VaultSlug = session.VaultSlug,
                FileName = session.FileName,
                TotalBytes = session.TotalBytes,
                ChunkSize = session.ChunkSize,
                TotalParts = session.TotalParts,
                ReceivedParts = receivedParts,
                MissingParts = missingParts.Take(100).ToList(),
                BytesReceived = receivedParts.Sum(p => GetPartSize(uploadId, p)),
                StartedAt = session.StartedAt,
                ExpiresAt = session.ExpiresAt,
                CompletedAt = session.CompletedAt,
                VaultPath = session.VaultPath,
                FinalSha256 = session.FinalSha256,
                ErrorCode = session.ErrorCode
            };
        }

        public void CompleteUpload(string uploadId, string fileSha256)
        {
            var session = GetSession(uploadId);
            if (session == null)
                throw new FileNotFoundException("Upload session not found");

            if (session.Status == "READY" || session.Status == "ASSEMBLING")
                return; // Ya completado o en proceso

            var receivedParts = GetReceivedParts(uploadId);
            if (receivedParts.Count < session.TotalParts)
            {
                throw new InvalidOperationException($"Missing parts: {session.TotalParts - receivedParts.Count}");
            }

            UpdateSessionStatus(uploadId, "ASSEMBLING");

            try
            {
                // Ensamblar archivo
                string vaultPath = GetVaultPath(session.VaultSlug);
                string destFile = Path.Combine(vaultPath, session.FileName);
                
                // Evitar sobrescribir
                if (File.Exists(destFile))
                {
                    string ext = Path.GetExtension(session.FileName);
                    string name = Path.GetFileNameWithoutExtension(session.FileName);
                    destFile = Path.Combine(vaultPath, $"{name}.{session.UploadId}{ext}");
                }

                // Archivo temporal
                string tempFile = destFile + ".assembling";

                using (var output = File.Create(tempFile))
                using (var sha256 = SHA256.Create())
                {
                    for (int i = 1; i <= session.TotalParts; i++)
                    {
                        string stagingPath = Path.Combine(_stagingRoot, uploadId, $"part_{i:D5}.bin");
                        if (!File.Exists(stagingPath))
                            throw new FileNotFoundException($"Part {i} not found in staging");

                        byte[] partBytes = File.ReadAllBytes(stagingPath);
                        
                        // Descomprimir si es necesario
                        var partInfo = GetPart(uploadId, i);
                        byte[] rawBytes = partBytes;
                        
                        if (partInfo?.Compression == "zstd")
                        {
                            // Aquí iría la descompresión Zstd
                            // Para WebForms legacy, usamos el archivo tal cual
                            // En producción necesitarías ZstdSharp.Port
                        }
                        
                        output.Write(rawBytes, 0, rawBytes.Length);
                        sha256.TransformFinalBlock(rawBytes, 0, rawBytes.Length);
                    }
                }

                string computedHash = ComputeSha256Hex(File.ReadAllBytes(tempFile));
                if (!string.Equals(computedHash, fileSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempFile);
                    UpdateSessionStatus(uploadId, "FAILED", "global_hash_mismatch", "SHA-256 mismatch after assembly");
                    throw new InvalidOperationException("Global SHA-256 mismatch");
                }

                // Mover a destino final
                File.Move(tempFile, destFile, true);

                // Limpiar staging
                string stagingFolder = Path.Combine(_stagingRoot, uploadId);
                if (Directory.Exists(stagingFolder))
                    Directory.Delete(stagingFolder, true);

                // Actualizar sesión
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            UPDATE upload_sessions SET 
                                status = 'READY',
                                vault_path = @vault_path,
                                final_sha256 = @final_sha256,
                                completed_at = @completed_at
                            WHERE upload_id = @upload_id";
                        
                        cmd.Parameters.AddWithValue("@vault_path", destFile);
                        cmd.Parameters.AddWithValue("@final_sha256", computedHash);
                        cmd.Parameters.AddWithValue("@completed_at", DateTime.UtcNow.ToString("o"));
                        cmd.Parameters.AddWithValue("@upload_id", uploadId);
                        
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateSessionStatus(uploadId, "FAILED", "assembly_error", ex.Message);
                throw;
            }
        }

        private UploadSession GetSession(string uploadId)
        {
            var dt = DatabaseHelper.ExecuteQuery("SELECT * FROM upload_sessions WHERE upload_id = @id",
                new SQLiteParameter("@id", uploadId));
            if (dt.Rows.Count == 0) return null;

            var row = dt.Rows[0];
            return new UploadSession
            {
                UploadId = row["upload_id"].ToString(),
                VaultSlug = row["vault_slug"].ToString(),
                FileName = row["file_name"].ToString(),
                TotalBytes = Convert.ToInt64(row["total_bytes"]),
                ChunkSize = Convert.ToInt64(row["chunk_size"]),
                TotalParts = Convert.ToInt32(row["total_parts"]),
                ExpectedSha256 = row["expected_sha256"]?.ToString(),
                Status = row["status"].ToString(),
                ClientId = row["client_id"]?.ToString(),
                ClientPlatform = row["client_platform"]?.ToString(),
                VaultPath = row["vault_path"]?.ToString(),
                FinalSha256 = row["final_sha256"]?.ToString(),
                ErrorCode = row["error_code"]?.ToString(),
                ErrorMessage = row["error_message"]?.ToString(),
                StartedAt = DateTime.Parse(row["started_at"].ToString()),
                ExpiresAt = DateTime.Parse(row["expires_at"].ToString()),
                CompletedAt = row["completed_at"] != DBNull.Value ? DateTime.Parse(row["completed_at"].ToString()) : (DateTime?)null
            };
        }

        private UploadPart GetPart(string uploadId, int partNumber)
        {
            var dt = DatabaseHelper.ExecuteQuery(
                "SELECT * FROM upload_parts WHERE upload_id = @id AND part_number = @num",
                new SQLiteParameter("@id", uploadId),
                new SQLiteParameter("@num", partNumber));
            if (dt.Rows.Count == 0) return null;

            var row = dt.Rows[0];
            return new UploadPart
            {
                UploadId = row["upload_id"].ToString(),
                PartNumber = Convert.ToInt32(row["part_number"]),
                RawOffset = Convert.ToInt64(row["raw_offset"]),
                RawSize = Convert.ToInt64(row["raw_size"]),
                CompressedSize = Convert.ToInt64(row["compressed_size"]),
                Compression = row["compression"].ToString(),
                ChunkSha256 = row["chunk_sha256"].ToString(),
                RawSha256 = row["raw_sha256"]?.ToString(),
                ConfirmedAt = DateTime.Parse(row["confirmed_at"].ToString())
            };
        }

        private List<int> GetReceivedParts(string uploadId)
        {
            var dt = DatabaseHelper.ExecuteQuery(
                "SELECT part_number FROM upload_parts WHERE upload_id = @id ORDER BY part_number",
                new SQLiteParameter("@id", uploadId));
            return dt.Rows.Cast<DataRow>().Select(r => Convert.ToInt32(r["part_number"])).ToList();
        }

        private long GetPartSize(string uploadId, int partNumber)
        {
            var part = GetPart(uploadId, partNumber);
            return part?.CompressedSize ?? 0;
        }

        private void UpdateSessionStatus(string uploadId, string status, string errorCode = null, string errorMessage = null)
        {
            DatabaseHelper.ExecuteNonQuery(
                "UPDATE upload_sessions SET status = @status, error_code = @ecode, error_message = @emsg WHERE upload_id = @id",
                new SQLiteParameter("@status", status),
                new SQLiteParameter("@ecode", errorCode ?? (object)DBNull.Value),
                new SQLiteParameter("@emsg", errorMessage ?? (object)DBNull.Value),
                new SQLiteParameter("@id", uploadId)
            );
        }

        private string GetVaultPath(string vaultSlug)
        {
            var dt = DatabaseHelper.ExecuteQuery(
                "SELECT path FROM vaults WHERE slug = @slug AND is_active = 1",
                new SQLiteParameter("@slug", vaultSlug));
            if (dt.Rows.Count == 0) return null;

            string relativePath = dt.Rows[0]["path"].ToString();
            return Path.Combine(_vaultsRoot, relativePath);
        }

        private bool WouldExceedQuota(string vaultSlug, long bytes)
        {
            var dt = DatabaseHelper.ExecuteQuery(
                "SELECT quota_gb FROM vaults WHERE slug = @slug",
                new SQLiteParameter("@slug", vaultSlug));
            if (dt.Rows.Count == 0) return true;

            long quotaBytes = Convert.ToInt64(dt.Rows[0]["quota_gb"]) * 1024 * 1024 * 1024;

            // Calcular uso actual
            var dt2 = DatabaseHelper.ExecuteQuery(
                "SELECT SUM(total_bytes) FROM upload_sessions WHERE vault_slug = @slug AND status = 'READY'",
                new SQLiteParameter("@slug", vaultSlug));
            long used = dt2.Rows[0][0] != DBNull.Value ? Convert.ToInt64(dt2.Rows[0][0]) : 0;

            return used + bytes > quotaBytes;
        }

        private bool IsUnderPressure()
        {
            // Verificar uso de disco
            try
            {
                var drive = new DriveInfo(_stagingRoot);
                if (drive.IsReady)
                {
                    double usedPercent = 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
                    return usedPercent > _backpressurePercent;
                }
            }
            catch { }
            return false;
        }

        private string ComputeSha256Hex(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private class UploadPart
        {
            public string UploadId { get; set; }
            public int PartNumber { get; set; }
            public long RawOffset { get; set; }
            public long RawSize { get; set; }
            public long CompressedSize { get; set; }
            public string Compression { get; set; }
            public string ChunkSha256 { get; set; }
            public string RawSha256 { get; set; }
            public DateTime ConfirmedAt { get; set; }
        }
    }
}