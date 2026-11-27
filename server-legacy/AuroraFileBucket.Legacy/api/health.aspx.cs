using System;
using System.Web;
using System.Web.Services;
using Newtonsoft.Json;
using AuroraFileBucket.Legacy.Services;

namespace AuroraFileBucket.Legacy.Api
{
    public partial class Health : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            Response.ContentType = "application/json; charset=utf-8";
            
            try
            {
                // Verificar BD
                using (var conn = DatabaseHelper.GetConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT 1";
                        cmd.ExecuteScalar();
                    }
                }
                
                // Verificar carpetas
                string stagingRoot = System.Configuration.ConfigurationManager.AppSettings["StagingRoot"] 
                    ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "Staging");
                string vaultsRoot = System.Configuration.ConfigurationManager.AppSettings["VaultsRoot"] 
                    ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "Vaults");
                
                bool stagingOk = System.IO.Directory.Exists(stagingRoot);
                bool vaultsOk = System.IO.Directory.Exists(vaultsRoot);
                
                // Verificar espacio en disco
                var drive = new System.IO.DriveInfo(stagingRoot);
                long freeSpace = drive.IsReady ? drive.AvailableFreeSpace : 0;
                
                var health = new
                {
                    status = "healthy",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    version = "1.0.0-legacy",
                    framework = ".NET Framework 4.8",
                    checks = new
                    {
                        database = "ok",
                        staging_directory = stagingOk ? "ok" : "missing",
                        vaults_directory = vaultsOk ? "ok" : "missing",
                        free_space_bytes = freeSpace
                    }
                };
                
                Response.Write(JsonConvert.SerializeObject(health, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Response.StatusCode = 503;
                Response.Write(JsonConvert.SerializeObject(new
                {
                    status = "unhealthy",
                    error = ex.Message
                }));
            }
        }
    }
}