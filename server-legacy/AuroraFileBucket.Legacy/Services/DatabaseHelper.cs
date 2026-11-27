using System;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Configuration;

namespace AuroraFileBucket.Legacy.Services
{
    public static class DatabaseHelper
    {
        private static readonly string ConnectionString = ConfigurationManager.ConnectionStrings["AuroraDB"].ConnectionString;
        private static readonly string DataDirectory = AppDomain.CurrentDomain.GetData("DataDirectory")?.ToString() ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data");

        public static void InitializeDatabase()
        {
            string dbPath = Path.Combine(DataDirectory, "AuroraLegacy.db");
            
            // Asegurar que existe el directorio
            if (!Directory.Exists(DataDirectory))
                Directory.CreateDirectory(DataDirectory);

            using (var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                conn.Open();
                
                // Tabla de sesiones de subida
                string createSessions = @"
                    CREATE TABLE IF NOT EXISTS upload_sessions (
                        upload_id TEXT PRIMARY KEY,
                        vault_slug TEXT NOT NULL,
                        file_name TEXT NOT NULL,
                        total_bytes INTEGER NOT NULL,
                        chunk_size INTEGER NOT NULL,
                        total_parts INTEGER NOT NULL,
                        expected_sha256 TEXT,
                        status TEXT NOT NULL DEFAULT 'INIT',
                        client_id TEXT,
                        client_platform TEXT,
                        vault_path TEXT,
                        final_sha256 TEXT,
                        error_code TEXT,
                        error_message TEXT,
                        started_at TEXT NOT NULL,
                        expires_at TEXT NOT NULL,
                        completed_at TEXT
                    );";
                
                // Tabla de partes
                string createParts = @"
                    CREATE TABLE IF NOT EXISTS upload_parts (
                        upload_id TEXT NOT NULL,
                        part_number INTEGER NOT NULL,
                        raw_offset INTEGER NOT NULL,
                        raw_size INTEGER NOT NULL,
                        compressed_size INTEGER NOT NULL,
                        compression TEXT NOT NULL,
                        chunk_sha256 TEXT NOT NULL,
                        raw_sha256 TEXT,
                        confirmed_at TEXT NOT NULL,
                        PRIMARY KEY (upload_id, part_number)
                    );";
                
                // Tabla de refresh tokens
                string createTokens = @"
                    CREATE TABLE IF NOT EXISTS refresh_tokens (
                        token_hash TEXT PRIMARY KEY,
                        user_email TEXT NOT NULL,
                        expires_at TEXT NOT NULL,
                        revoked_at TEXT,
                        replaced_by TEXT
                    );";
                
                // Tabla de usuarios
                string createUsers = @"
                    CREATE TABLE IF NOT EXISTS users (
                        email TEXT PRIMARY KEY,
                        password_hash TEXT NOT NULL,
                        display_name TEXT,
                        is_active INTEGER NOT NULL DEFAULT 1,
                        created_at TEXT NOT NULL
                    );";
                
                // Tabla de bóvedas
                string createVaults = @"
                    CREATE TABLE IF NOT EXISTS vaults (
                        slug TEXT PRIMARY KEY,
                        path TEXT NOT NULL,
                        quota_gb INTEGER NOT NULL DEFAULT 500,
                        is_active INTEGER NOT NULL DEFAULT 1
                    );";
                
                // Tabla de relación usuario-bóveda
                string createUserVaults = @"
                    CREATE TABLE IF NOT EXISTS user_vaults (
                        email TEXT NOT NULL,
                        vault_slug TEXT NOT NULL,
                        PRIMARY KEY (email, vault_slug)
                    );";

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = createSessions;
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = createParts;
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = createTokens;
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = createUsers;
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = createVaults;
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = createUserVaults;
                    cmd.ExecuteNonQuery();
                }

                // Insertar bóveda por defecto y usuario admin si no existen
                SeedDefaultData(conn);
            }
        }

        private static void SeedDefaultData(SQLiteConnection conn)
        {
            // Verificar si existe bóveda por defecto
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM vaults WHERE slug = 'default'";
                int count = Convert.ToInt32(cmd.ExecuteScalar());
                
                if (count == 0)
                {
                    cmd.CommandText = "INSERT INTO vaults (slug, path, quota_gb, is_active) VALUES ('default', 'default', 500, 1)";
                    cmd.ExecuteNonQuery();
                }
            }

            // Verificar si existe usuario admin
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM users WHERE email = 'admin@aurora.local'";
                int count = Convert.ToInt32(cmd.ExecuteScalar());
                
                if (count == 0)
                {
                    // Hash simple para 'admin123'
                    string passwordHash = BCrypt.Net.BCrypt.HashPassword("admin123");
                    cmd.CommandText = "INSERT INTO users (email, password_hash, display_name, is_active, created_at) VALUES ('admin@aurora.local', @hash, 'Administrator', 1, @now)";
                    cmd.Parameters.AddWithValue("@hash", passwordHash);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();

                    // Asignar bóveda default al admin
                    cmd.CommandText = "INSERT INTO user_vaults (email, vault_slug) VALUES ('admin@aurora.local', 'default')";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static SQLiteConnection GetConnection()
        {
            string dbPath = Path.Combine(DataDirectory, "AuroraLegacy.db");
            return new SQLiteConnection($"Data Source={dbPath};Version=3;");
        }

        public static void ExecuteNonQuery(string sql, params SQLiteParameter[] parameters)
        {
            using (var conn = GetConnection())
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    if (parameters != null)
                        cmd.Parameters.AddRange(parameters);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static T ExecuteScalar<T>(string sql, params SQLiteParameter[] parameters)
        {
            using (var conn = GetConnection())
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    if (parameters != null)
                        cmd.Parameters.AddRange(parameters);
                    object result = cmd.ExecuteScalar();
                    return result == null || result == DBNull.Value ? default(T) : (T)Convert.ChangeType(result, typeof(T));
                }
            }
        }

        public static DataTable ExecuteQuery(string sql, params SQLiteParameter[] parameters)
        {
            using (var conn = GetConnection())
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    if (parameters != null)
                        cmd.Parameters.AddRange(parameters);
                    using (var adapter = new SQLiteDataAdapter(cmd))
                    {
                        var dt = new DataTable();
                        adapter.Fill(dt);
                        return dt;
                    }
                }
            }
        }
    }
}