<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="AuroraFileBucket.Legacy.Default" %>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title>Aurora File Bucket - Servidor Legacy</title>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <style>
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }
        .container { max-width: 800px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        h1 { color: #2c3e50; margin-bottom: 10px; }
        .subtitle { color: #7f8c8d; margin-bottom: 30px; }
        .card { border: 1px solid #ecf0f1; border-radius: 6px; padding: 20px; margin-bottom: 20px; background: #fafafa; }
        .card h3 { margin-top: 0; color: #34495e; }
        .btn { display: inline-block; padding: 10px 20px; background: #3498db; color: white; text-decoration: none; border-radius: 4px; margin-right: 10px; }
        .btn:hover { background: #2980b9; }
        .btn-success { background: #27ae60; }
        .btn-success:hover { background: #229954; }
        .code { background: #2c3e50; color: #ecf0f1; padding: 15px; border-radius: 4px; font-family: 'Consolas', monospace; font-size: 14px; overflow-x: auto; white-space: pre-wrap; }
        .status-ok { color: #27ae60; font-weight: bold; }
        .status-error { color: #e74c3c; font-weight: bold; }
    </style>
</head>
<body>
    <form id="form1" runat="server">
        <div class="container">
            <h1>Aurora File Bucket <span style="font-size: 18px; color: #95a5a6;">(Legacy WebForms .NET 4.8)</span></h1>
            <p class="subtitle">Servidor de transferencia de archivos masivos (hasta 100 GB) - WebForms .NET Framework 4.8</p>

            <div class="card">
                <h3>Estado del servidor</h3>
                <p>Servidor: <span id="serverStatus" class="status-ok">En línea</span></p>
                <p>Versión: .NET Framework 4.8 (WebForms)</p>
                <p>Hora del servidor: <span id="serverTime"><%= DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") %> UTC</span></p>
            </div>

            <div class="card">
                <h3>Endpoints disponibles</h3>
                <p>El servidor implementa los siguientes endpoints compatibles con el protocolo Aurora:</p>
                <div class="code">
POST /api/auth/login          - Iniciar sesión y obtener token JWT
POST /api/auth/refresh        - Renovar token de acceso
POST /api/uploads/init        - Inicializar sesión de subida
PUT  /api/uploads/{id}/parts/{n} - Subir chunk (parte)
GET  /api/uploads/{id}/status - Consultar estado
POST /api/uploads/{id}/complete - Completar y ensamblar
                </div>
            </div>

            <div class="card">
                <h3>Pruebas rápidas</h3>
                <a href="Upload.aspx" class="btn">Ir a página de subida</a>
                <a href="api/health" class="btn btn-success">Health Check</a>
            </div>

            <div class="card">
                <h3>Configuración requerida</h3>
                <p>Para que el servidor funcione correctamente, asegúrate de tener configurado:</p>
                <ul>
                    <li><strong>Web.config</strong> - Configuración de IIS y base de datos</li>
                    <li><strong>Base de datos SQLite</strong> - Se crea automáticamente en App_Data/AuroraLegacy.db</li>
                    <li><strong>Carpetas de almacenamiento</strong> - Staging, Vaults, Data (se crean automáticamente)</li>
                    <li><strong>Clave JWT</strong> - Configurar en appSettings: <code>JwtSecret</code> (mínimo 32 caracteres)</li>
                    <li><strong>Usuario admin</strong> - Por defecto: admin@aurora.local / admin123</li>
                </ul>
            </div>
        </div>
    </form>
    <script>
        // Actualizar hora cada minuto
        setInterval(function() {
            document.getElementById('serverTime').textContent = new Date().toISOString().replace('T', ' ').substring(0, 19) + ' UTC';
        }, 60000);
    </script>
</body>
</html>