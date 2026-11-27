# Aurora File Bucket

Monorepo **open source** para transferencia **fiable**, **reanudable** y verificada de integridad de archivos masivos (hasta **100 GB**) hacia un servidor en la nube ejecutando **Windows Server + IIS + ASP.NET**.

## Estructura del repositorio

| Directorio | Propósito |
| :--- | :--- |
| `server-dotnet/` | Receptor ASP.NET Core en IIS: ingestión de chunks por streaming, descompresión Zstandard, verificación SHA-256, limpieza inmediata de staging, aislamiento por bóvedas y dashboard administrativo en tiempo real con SignalR. |
| `client-dotnet/` | Cliente CLI nativo en .NET 8/9 para estaciones de trabajo y servidores **Windows**. |
| `client-python/` | Cliente CLI en Python 3.12+ para servidores y pipelines **Linux**. |
| `landing-page/` | Página estática publicada automáticamente en **GitHub Pages** (`github.io`). |
| `contracts/openapi.yaml` | Contrato HTTP v1 compartido: plano de control, subida de chunks y refresco de tokens rotativos. |
| `docs/SPEC.md` | Especificación técnica completa. |
| `docs/PROTOCOL.md` | Protocolo normativo: cabeceras, formas JSON, códigos de estado y reglas de reintento. |
| `docs/ROADMAP.md` | Hoja de ruta v0.2-v0.3 con objetivos claros. |

## Uso rápido

```bash
# Linux (cliente Python)
aurora-send --server https://files.example.com \
            --file /data/big.iso \
            --email user@example.com --password secret

# Windows (cliente .NET)
dotnet run --project client-dotnet -- \
  --server https://files.example.com \
  --file C:\data\big.iso \
  --email user@example.com --password secret
```

## Características clave

- **Memoria constante:** menos de 64 MB por transferencia, nunca carga el archivo completo en RAM.
- **Verificado bit a bit:** SHA-256 se verifica por chunk y nuevamente sobre el archivo completo antes de aceptarlo.
- **Reanudable:** si la conexión se cae, el cliente continúa desde el último chunk confirmado por el servidor.
- **Compresión adaptativa:** Zstandard a nivel 3 con corte al 95%; archivos ya comprimidos pueden enviarse sin comprimir.
- **Control de ancho de banda:** token bucket global que limita el Mbps para no saturar la red local.
- **Token rotativo:** JWT efímero de 20 minutos + refresh token de un solo uso.
- **Aislamiento por bóvedas:** cada proyecto o cliente en su propia bóveda con cuota configurada.

## Desarrollo y pruebas

```bash
# .NET
dotnet build aurora-file-bucket.sln
dotnet test aurora-file-bucket.sln

# Python
python -m pip install -e ./client-python
python -m pytest client-python/tests -q

# End-to-end (prueba real con servidor + cliente + archivo)
python scripts/e2e.py
```

## Instalación del servidor

### Opción 1: Archivo publicado (recomendado)
1. Descarga `AuroraFileBucket.Server-win-x64.zip` desde `https://github.com/gmolina75/aurora-file-bucket/releases`
2. Extrae en `C:\aurora-server\`
3. Crea `appsettings.Local.json` en esa carpeta:
```json
{
  "AuroraStorage": {
    "StagingRoot": "C:\\aurora-staging",
    "VaultsRoot": "C:\\aurora-vaults",
    "DatabasePath": "aurora.db"
  },
  "Auth": {
    "JwtSigningKey": "TU_CLAVE_SECRETA",
    "Users": [{
      "email": "tu@email.com",
      "password": "tu_contrasena",
      "displayName": "Tu Nombre",
      "vaults": ["default"]
    }]
  }
}
```
4. Ejecuta: `AuroraFileBucket.Server.exe`

### Opción 2: Compilar desde código
```bash
git clone https://github.com/gmolina75/aurora-file-bucket.git
cd aurora-file-bucket
dotnet publish server-dotnet/AuroraFileBucket.Server.csproj -c Release -r win-x64
```

## Documentación

- `docs/PROTOCOL.md` — Protocolo normativo del wire (contrato).
- `docs/SPEC.md` — Especificación técnica completa.
- `contracts/openapi.yaml` — API contract en formato machine-readable.

## Licencia

MIT — ver [LICENSE](LICENSE). Para vulnerabilidades, ver [SECURITY.md](SECURITY.md).

## Créditos

Diseñado y desarrollado por Giancarlo Molina como parte del proyecto Mavis Code. Visto en [GitHub](https://github.com/gmolina75/aurora-file-bucket).