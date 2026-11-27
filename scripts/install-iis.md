# Instalación en IIS - Aurora File Bucket

## Prerequisitos

- Windows Server con IIS instalado
- .NET 8.0 Hosting Bundle instalado en el servidor
- Puerto 80/443 libre (o usar puerto alternativo)

## Paso 1: Publicar el sitio

En el servidor o desde tu workstation:

```powershell
cd C:\ruta\del\repo
dotnet publish server-dotnet/AuroraFileBucket.Server.csproj -c Release -o .\publish\iis-deploy
```

## Paso 2: Copiar archivos al servidor

Copias la carpeta `publish\iis-deploy` a tu servidor, por ejemplo:

```
D:\inetpub\wwwroot\aurora-file-bucket\
```

## Paso 3: Configurar appsettings.Local.json

Creas `D:\inetpub\wwwroot\aurora-file-bucket\appsettings.Local.json` con tu configuración:

```json
{
  "AuroraStorage": {
    "StagingRoot": "D:\\aurora-staging",
    "VaultsRoot": "D:\\aurora-vaults",
    "DatabasePath": "D:\\aurora-data\\aurora.db"
  },
  "Auth": {
    "JwtSigningKey": "GENERA_UNA_CLAVE_AQUI",
    "Users": [
      {
        "email": "tu@email.com",
        "password": "CONTRASENA_SEGURA",
        "displayName": "Tu Nombre",
        "vaults": ["default"]
      }
    ]
  }
}
```

**Generar clave JWT:**
```powershell
# PowerShell - genera una cadena aleatoria de 32+ caracteres
-join ((65..90) + (97..122) + (48..57) | Get-Random -Count 32 | % {[char]$_})
```

## Paso 4: Crear directorios y permisos

```powershell
# Crear carpetas
New-Item -ItemType Directory -Path "D:\aurora-staging", "D:\aurora-vaults", "D:\aurora-data"

# Dar permisos al Application Pool Identity
# (ejemplo con NETWORK SERVICE, ajusta según tu App Pool Identity)
icacls "D:\aurora-staging" /grant "IIS AppPool\AuroraPool:(OI)(CI)F"
icacls "D:\aurora-vaults" /grant "IIS AppPool\AuroraPool:(OI)(CI)F"
icacls "D:\aurora-data" /grant "IIS AppPool\AuroraPool:(OI)(CI)F"
```

## Paso 5: Configurar Application Pool en IIS

1. Abre **IIS Manager**
2. En **Application Pools**, crea un nuevo pool:
   - Nombre: `AuroraPool`
   - .NET CLR version: `No Managed Code`
   - Managed pipeline mode: `Integrated`
   - Start Mode: `AlwaysRunning`
   - Idle Time-out: `0 (zero)`
   - Recycling: deshabilitado o alto intervalo

3. En **Handler Mappings**, verifica que los verbos `PUT`, `PATCH`, `DELETE` estén permitidos

## Paso 6: Crear el sitio web

1. En IIS Manager, **Add Website**
2. Site name: `Aurora File Bucket`
3. Physical path: `D:\inetpub\wwwroot\aurora-file-bucket`
4. Binding: `http` (o `https` con certificado)
5. Application Pool: `AuroraPool`

## Paso 7: Verificar web.config

El archivo `web.config` ya está configurado para:
- Máximo tamaño de contenido: 36 MB (para chunks)
- Verbos HTTP necesarios: PUT, PATCH, HEAD, DELETE
- WebDAV removido

## Paso 8: Probar

Desde tu cliente:

```bash
aurora-send --server http://tu-servidor --file archivo.mp4 --email tu@email.com --password tuclave
```

## Solución de problemas

- **401 Unauthorized**: Verifica que `appsettings.Local.json` esté en la carpeta correcta
- **500 Internal Server Error**: Revisa el Event Viewer de Windows > Applications
- **No auth**: El archivo debe tener extensión `.json` y estar bien formado
- **Permisos**: Asegúrate de que el App Pool Identity escriba en staging, vaults y data

## Seguridad en producción

- Configura HTTPS con certificado válido
- Usa una clave JWT larga y aleatoria
- Considera usar reverse proxy (NGINX, Cloudflare) para rate limiting
- Habilita `TelegramNotificationBotTokenEnvVar` para alertas automáticas