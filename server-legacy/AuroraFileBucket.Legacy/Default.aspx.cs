using System;

namespace AuroraFileBucket.Legacy
{
    public partial class Default : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            // Verificar salud del servidor
            if (!IsPostBack)
            {
                // Inicializar base de datos si no existe
                try
                {
                    Services.DatabaseHelper.InitializeDatabase();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("Error inicializando BD: " + ex.Message);
                }
            }
        }
    }
}