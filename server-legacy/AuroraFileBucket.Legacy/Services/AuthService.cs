using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using AuroraFileBucket.Legacy.Services;

namespace AuroraFileBucket.Legacy.Services
{
    public class TokenPair
    {
        public string TokenType { get; set; } = "Bearer";
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public UserInfo User { get; set; }
        public List<string> Vaults { get; set; }
    }

    public class UserInfo
    {
        public string Email { get; set; }
        public string DisplayName { get; set; }
    }

    public class AuthService
    {
        private readonly string _jwtSecret;
        private readonly int _accessTokenMinutes;
        private readonly int _refreshTokenHours;
        private readonly string _issuer;
        private readonly string _audience;

        public AuthService()
        {
            _jwtSecret = ConfigurationManager.AppSettings["JwtSecret"] ?? "CHANGE_THIS_IN_PRODUCTION_MUST_BE_AT_LEAST_32_CHARS_LONG_FOR_HS256";
            _accessTokenMinutes = int.Parse(ConfigurationManager.AppSettings["AccessTokenMinutes"] ?? "20");
            _refreshTokenHours = int.Parse(ConfigurationManager.AppSettings["RefreshTokenHours"] ?? "24");
            _issuer = ConfigurationManager.AppSettings["JwtIssuer"] ?? "aurora-file-bucket";
            _audience = ConfigurationManager.AppSettings["JwtAudience"] ?? "aurora-file-bucket";
        }

        public TokenPair Login(string email, string password)
        {
            var user = GetUserByEmail(email);
            if (user == null || !VerifyPassword(password, user.PasswordHash))
            {
                return null;
            }

            if (!user.IsActive)
            {
                return null;
            }

            var vaults = GetUserVaults(email);
            return IssueTokens(user.Email, user.DisplayName, vaults);
        }

        public TokenPair RefreshToken(string refreshToken)
        {
            string hash = HashToken(refreshToken);
            var tokenData = GetRefreshToken(hash);
            
            if (tokenData == null || tokenData.RevokedAt != null)
            {
                // Token ya revocado - revocar todos los del usuario
                if (tokenData != null)
                {
                    RevokeAllUserTokens(tokenData.UserEmail);
                }
                return null;
            }

            if (DateTime.UtcNow > DateTime.Parse(tokenData.ExpiresAt))
            {
                RevokeToken(hash);
                return null;
            }

            var user = GetUserByEmail(tokenData.UserEmail);
            if (user == null || !user.IsActive)
            {
                return null;
            }

            var vaults = GetUserVaults(tokenData.UserEmail);
            var newPair = IssueTokens(user.Email, user.DisplayName, vaults);
            
            // Revocar el token usado y actualizar con el nuevo
            RevokeToken(hash, newPair.RefreshToken);
            
            return newPair;
        }

        public ClaimsPrincipal ValidateToken(string token)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_jwtSecret);
            
            try
            {
                var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = true,
                    ValidIssuer = _issuer,
                    ValidateAudience = true,
                    ValidAudience = _audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                }, out SecurityToken validatedToken);

                return principal;
            }
            catch
            {
                return null;
            }
        }

        public bool IsVaultAllowed(ClaimsPrincipal principal, string vaultSlug)
        {
            var vaultsClaim = principal.FindAll("vaults").Select(c => c.Value).ToList();
            return vaultsClaim.Contains(vaultSlug, StringComparer.OrdinalIgnoreCase);
        }

        private TokenPair IssueTokens(string email, string displayName, List<string> vaults)
        {
            var now = DateTime.UtcNow;
            var accessExpires = now.AddMinutes(_accessTokenMinutes);
            var refreshExpires = now.AddHours(_refreshTokenHours);

            // Access Token (JWT)
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim("name", string.IsNullOrWhiteSpace(displayName) ? email : displayName)
            };

            foreach (var vault in vaults)
            {
                claims.Add(new Claim("vaults", vault));
            }

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret)),
                SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                notBefore: now,
                expires: accessExpires,
                signingCredentials: credentials);

            var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

            // Refresh Token (opaco)
            var refreshToken = CreateOpaqueToken();
            var refreshHash = HashToken(refreshToken);

            StoreRefreshToken(refreshHash, email, refreshExpires);

            return new TokenPair
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresIn = (int)(accessExpires - now).TotalSeconds,
                User = new UserInfo { Email = email, DisplayName = displayName },
                Vaults = vaults
            };
        }

        private string CreateOpaqueToken()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[32];
                rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').Replace("=", "");
            }
        }

        private string HashToken(string token)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        private void StoreRefreshToken(string hash, string email, DateTime expiresAt)
        {
            DatabaseHelper.ExecuteNonQuery(
                "INSERT INTO refresh_tokens (token_hash, user_email, expires_at) VALUES (@hash, @email, @expires)",
                new System.Data.SQLite.SQLiteParameter("@hash", hash),
                new System.Data.SQLite.SQLiteParameter("@email", email),
                new System.Data.SQLite.SQLiteParameter("@expires", expiresAt.ToString("o"))
            );
        }

        private RefreshTokenData GetRefreshToken(string hash)
        {
            var dt = DatabaseHelper.ExecuteQuery("SELECT * FROM refresh_tokens WHERE token_hash = @hash",
                new System.Data.SQLite.SQLiteParameter("@hash", hash));
            if (dt.Rows.Count == 0) return null;
            
            var row = dt.Rows[0];
            return new RefreshTokenData
            {
                Hash = row["token_hash"].ToString(),
                UserEmail = row["user_email"].ToString(),
                ExpiresAt = row["expires_at"].ToString(),
                RevokedAt = row["revoked_at"] != DBNull.Value ? row["revoked_at"].ToString() : null,
                ReplacedBy = row["replaced_by"] != DBNull.Value ? row["replaced_by"].ToString() : null
            };
        }

        private void RevokeToken(string hash, string replacedBy = null)
        {
            DatabaseHelper.ExecuteNonQuery(
                "UPDATE refresh_tokens SET revoked_at = @now, replaced_by = @replaced WHERE token_hash = @hash",
                new System.Data.SQLite.SQLiteParameter("@now", DateTime.UtcNow.ToString("o")),
                new System.Data.SQLite.SQLiteParameter("@replaced", replacedBy ?? (object)DBNull.Value),
                new System.Data.SQLite.SQLiteParameter("@hash", hash)
            );
        }

        private void RevokeAllUserTokens(string email)
        {
            DatabaseHelper.ExecuteNonQuery(
                "UPDATE refresh_tokens SET revoked_at = @now WHERE user_email = @email AND revoked_at IS NULL",
                new System.Data.SQLite.SQLiteParameter("@now", DateTime.UtcNow.ToString("o")),
                new System.Data.SQLite.SQLiteParameter("@email", email)
            );
        }

        private UserRecord GetUserByEmail(string email)
        {
            var dt = DatabaseHelper.ExecuteQuery("SELECT * FROM users WHERE email = @email",
                new System.Data.SQLite.SQLiteParameter("@email", email));
            if (dt.Rows.Count == 0) return null;
            
            var row = dt.Rows[0];
            return new UserRecord
            {
                Email = row["email"].ToString(),
                PasswordHash = row["password_hash"].ToString(),
                DisplayName = row["display_name"]?.ToString(),
                IsActive = Convert.ToInt32(row["is_active"]) == 1
            };
        }

        private List<string> GetUserVaults(string email)
        {
            var dt = DatabaseHelper.ExecuteQuery("SELECT vault_slug FROM user_vaults WHERE email = @email",
                new System.Data.SQLite.SQLiteParameter("@email", email));
            return dt.Rows.Cast<System.Data.DataRow>().Select(r => r["vault_slug"].ToString()).ToList();
        }

        private bool VerifyPassword(string password, string hash)
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch
            {
                return false;
            }
        }

        private class RefreshTokenData
        {
            public string Hash { get; set; }
            public string UserEmail { get; set; }
            public string ExpiresAt { get; set; }
            public string RevokedAt { get; set; }
            public string ReplacedBy { get; set; }
        }

        private class UserRecord
        {
            public string Email { get; set; }
            public string PasswordHash { get; set; }
            public string DisplayName { get; set; }
            public bool IsActive { get; set; }
        }
    }
}