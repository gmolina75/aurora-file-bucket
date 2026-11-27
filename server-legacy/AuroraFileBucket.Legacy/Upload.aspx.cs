using System;
using System.Web;
using System.Web.Services;
using Newtonsoft.Json;
using AuroraFileBucket.Legacy.Services;

namespace AuroraFileBucket.Legacy
{
    public partial class Upload : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                // Configurar CORS para APIs
                Response.AddHeader("Access-Control-Allow-Origin", "*");
                Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Chunk-Sha256, X-Raw-Size, X-Compression, X-Vault-Slug, X-Client-Id, X-Client-Platform");
            }
        }
    }

    // Web Services para la API REST
    [WebService(Namespace = "http://aurora-file-bucket.org/")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    [System.ComponentModel.ToolboxItem(false)]
    [System.Web.Script.Services.ScriptService]
    public class AuthApi : System.Web.Services.WebService
    {
        private readonly Services.AuthService _authService = new Services.AuthService();

        [WebMethod]
        [System.Web.Script.Services.ScriptMethod(UseHttpGet = false, ResponseFormat = ResponseFormat.Json)]
        public void Login()
        {
            try
            {
                string json = new System.IO.StreamReader(Context.Request.InputStream).ReadToEnd();
                dynamic request = JsonConvert.DeserializeObject(json);
                
                string email = request?.email;
                string password = request?.password;

                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                {
                    WriteError("invalid_credentials", "Email y contraseña requeridos", 401);
                    return;
                }

                var tokens = _authService.Login(email, password);
                if (tokens == null)
                {
                    WriteError("invalid_credentials", "Credenciales inválidas", 401);
                    return;
                }

                WriteJson(tokens);
            }
            catch (Exception ex)
            {
                WriteError("internal_error", ex.Message, 500);
            }
        }

        [WebMethod]
        [System.Web.Script.Services.ScriptMethod(UseHttpGet = false, ResponseFormat = ResponseFormat.Json)]
        public void Refresh()
        {
            try
            {
                string json = new System.IO.StreamReader(Context.Request.InputStream).ReadToEnd();
                dynamic request = JsonConvert.DeserializeObject(json);
                
                string refreshToken = request?.refreshToken;
                if (string.IsNullOrEmpty(refreshToken))
                {
                    WriteError("invalid_refresh_token", "Refresh token requerido", 401);
                    return;
                }

                var tokens = _authService.RefreshToken(refreshToken);
                if (tokens == null)
                {
                    WriteError("invalid_refresh_token", "Refresh token inválido o expirado", 401);
                    return;
                }

                WriteJson(tokens);
            }
            catch (Exception ex)
            {
                WriteError("internal_error", ex.Message, 500);
            }
        }

        private void WriteJson(object obj)
        {
            Context.Response.ContentType = "application/json";
            Context.Response.Write(JsonConvert.SerializeObject(obj));
        }

        private void WriteError(string code, string message, int statusCode)
        {
            Context.Response.StatusCode = statusCode;
            Context.Response.ContentType = "application/json";
            Context.Response.Write(JsonConvert.SerializeObject(new
            {
                error = new { code, message }
            }));
        }
    }

    [WebService(Namespace = "http://aurora-file-bucket.org/")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    [System.ComponentModel.ToolboxItem(false)]
    [System.Web.Script.Services.ScriptService]
    public class UploadApi : System.Web.Services.WebService
    {
        private readonly Services.StorageService _storageService = new Services.StorageService();
        private readonly Services.AuthService _authService = new Services.AuthService();

        [WebMethod]
        [System.Web.Script.Services.ScriptMethod(UseHttpGet = false, ResponseFormat = ResponseFormat.Json)]
        public void Init()
        {
            try
            {
                var principal = ValidateRequest();
                if (principal == null) return;

                string json = new System.IO.StreamReader(Context.Request.InputStream).ReadToEnd();
                dynamic request = JsonConvert.DeserializeObject(json);

                string fileName = request?.fileName;
                long totalBytes = request?.totalBytes ?? 0;
                long chunkSize = request?.chunkSize ?? 0;
                int totalParts = request?.totalParts ?? 0;
                string fileSha256 = request?.fileSha256;
                string compression = request?.compression ?? "auto";
                string vaultSlug = Context.Request.Headers["X-Vault-Slug"] ?? "default";

                var session = _storageService.InitUpload(
                    fileName, totalBytes, chunkSize, totalParts, fileSha256,
                    vaultSlug, principal.FindFirst("jti")?.Value, "webforms");

                WriteJson(session);
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteError("vault_not_allowed", ex.Message, 403);
            }
            catch (InvalidOperationException ex)
            {
                WriteError("quota_exceeded", ex.Message, 413);
            }
            catch (Exception ex)
            {
                WriteError("internal_error", ex.Message, 500);
            }
        }

        [WebMethod]
        [System.Web.Script.Services.ScriptMethod(UseHttpGet = false, ResponseFormat = ResponseFormat.Json)]
        public void PutPart(string id, int partNumber)
        {
            try
            {
                var principal = ValidateRequest();
                if (principal == null) return;

                string chunkSha256 = Context.Request.Headers["X-Chunk-Sha256"];
                string compression = Context.Request.Headers["X-Compression"] ?? "none";
                long rawSize = long.Parse(Context.Request.Headers["X-Raw-Size"] ?? "0");
                string rawSha256 = Context.Request.Headers["X-Raw-Sha256"];

                if (string.IsNullOrEmpty(chunkSha256))
                {
                    WriteError("invalid_request", "X-Chunk-Sha256 header requerido", 400);
                    return;
                }

                // Leer el stream del archivo
                var stream = Context.Request.InputStream;
                var receipt = _storageService.PutPart(id, partNumber, stream, chunkSha256, compression, rawSize, rawSha256);
                
                WriteJson(receipt);
            }
            catch (FileNotFoundException)
            {
                WriteError("not_found", "Sesión de subida no encontrada", 404);
            }
            catch (InvalidOperationException ex)
            {
                WriteError("invalid_request", ex.Message, 400);
            }
            catch (Exception ex)
            {
                WriteError("internal_error", ex.Message, 500);
            }
        }

        [WebMethod]
        [System.Web.Script.Services.ScriptMethod(UseHttpGet = true, ResponseFormat = ResponseFormat.Json)]
        public void Status(string id)
        {
            try
            {
                var principal = ValidateRequest();
                if (principal == null) return;

                var status = _storageService.GetStatus(id);
                if (status == null)
                {
                    WriteError("not_found", "Sesión no encontrada", 404);
                    return;
                }

                WriteJson(status);
            }
            catch (Exception ex)
            {
                WriteError("internal_error", ex.Message, 500);
            }
        }

        [WebMethod]
        [System.Web.Script.Services.ScriptMethod(UseHttpGet = false, ResponseFormat = ResponseFormat.Json)]
        public void Complete(string id)
        {
            try
            {
                var principal = ValidateRequest();
                if (principal == null) return;

                string json = new System.IO.StreamReader(Context.Request.InputStream).ReadToEnd();
                dynamic request = JsonConvert.DeserializeObject(json);
                
                string fileSha256 = request?.fileSha256;
                bool notify = request?.notify ?? true;

                _storageService.CompleteUpload(id, fileSha256);
                
                WriteJson(new { status = "READY", message = "Archivo ensamblado y verificado" });
            }
            catch (FileNotFoundException)
            {
                WriteError("not_found", "Sesión no encontrada", 404);
            }
            catch (InvalidOperationException ex)
            {
                WriteError("incomplete_upload", ex.Message, 409);
            }
            catch (Exception ex)
            {
                WriteError("internal_error", ex.Message, 500);
            }
        }

        private System.Security.Claims.ClaimsPrincipal ValidateRequest()
        {
            string authHeader = Context.Request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                WriteError("unauthorized", "Token de acceso requerido", 401);
                return null;
            }

            string token = authHeader.Substring("Bearer ".Length);
            var principal = _authService.ValidateToken(token);
            
            if (principal == null)
            {
                WriteError("unauthorized", "Token inválido o expirado", 401);
                return null;
            }

            return principal;
        }

        private void WriteJson(object obj)
        {
            Context.Response.ContentType = "application/json; charset=utf-8";
            Context.Response.Write(JsonConvert.SerializeObject(obj));
        }

        private void WriteError(string code, string message, int statusCode)
        {
            Context.Response.StatusCode = statusCode;
            Context.Response.ContentType = "application/json; charset=utf-8";
            Context.Response.Write(JsonConvert.SerializeObject(new
            {
                error = new { code, message }
            }));
            Context.Response.End();
        }
    }
}