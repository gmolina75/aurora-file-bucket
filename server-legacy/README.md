# Aurora File Bucket - Servidor Legacy (.NET Framework 4.8 WebForms)

Versión del servidor compatible con **ASP.NET WebForms** sobre **.NET Framework 4.8**, diseñada para ejecutarse directamente en **IIS clásico** sin necesidad de Hosting Bundle de .NET Core.

## Características

- ✅ **WebForms .NET Framework 4.8** - Compatible con IIS 7.0+
- ✅ **Sin dependencias de .NET Core** - Ejecuta nativamente en Windows Server
- ✅ **Base de datos SQLite** - Archivo local (`App_Data/AuroraLegacy.db`)
- ✅ **Autenticación JWT** - Tokens rotativos (access + refresh)
- ✅ **Subida fragmentada** - Chunks con hash SHA-256 y reanudación
- ✅ **Compresión opcional** - Zstd (requiere librería nativa) o sin comprimir
- ✅ **Bóvedas aisladas** - Cuotas y permisos por usuario
- ✅ **Interfaz WebForms** - Página de subida con progreso en tiempo real
- ✅ **API REST** - Endpoints compatibles con clientes .NET y Python existentes

## Requisitos

- Windows Server 2012 R2 / 2016 / 2019 / 2022
- IIS 7.0+ con ASP.NET 4.8 habilitado
- .NET Framework 4.8 instalado
- Visual Studio 2019/2022 para compilar (o MSBuild)

## Estructura del proyecto

```
server-legacy/
└── AuroraFileBucket.Legacy/
    ├── AuroraFileBucket.Legacy.csproj
    ├── Web.config
    ├── Global.asax / Global.asax.cs
    ├── Default.aspx / Default.aspx.cs          # Página principal
    ├── Upload.aspx / Upload.aspx.cs            # Interfaz de subida
    ├── packages.config                         # Dependencias NuGet
    ├── Services/
    │   ├── AuthService.cs                      # JWT + Refresh tokens
    │   ├── StorageService.cs                   # Lógica de almacenamiento
    │   └── DatabaseHelper.cs                   # SQLite + EF6
    └── Api/
        └── health.aspx / health.aspx.cs        # Health check
```

## Instalación

### 1. Abrir en Visual Studio

```cmd
# Abrir la solución
devenv AuroraFileBucket.Legacy.csproj
```

### 2. Restaurar paquetes NuGet

En Visual Studio: **Tools > NuGet Package Manager > Restore NuGet Packages**

O desde línea de comandos:
```cmd
nuget restore AuroraFileBucket.Legacy.csproj
```

### 3. Compilar

```cmd
msbuild AuroraFileBucket.Legacy.csproj /p:Configuration=Release
```

### 4. Publicar en IIS

#### Opción A: Publicar desde Visual Studio
1. Click derecho en el proyecto > **Publish**
2. Target: **Folder**
3. Folder: `C:\inetpub\wwwroot\AuroraFileBucket`
4. Configuration: **Release**
5. Click **Publish**

#### Opción B: Copiar archivos manualmente
```cmd
# Copiar carpeta bin\Release y archivos .aspx, Web.config, Global.asax
xcopy bin\Release\*.* C:\inetpub\wwwroot\AuroraFileBucket\bin\ /E /Y
xcopy *.aspx *.aspx.cs Global.asax Global.asax.cs Web.config C:\inetpub\wwwroot\AuroraFileBucket\ /Y
xcopy Services\*.cs C:\inetpub\wwwroot\AuroraFileBucket\Services\ /Y
xcopy Api\*.aspx Api\*.aspx.cs C:\inetpub\wwwroot\AuroraFileBucket\Api\ /Y
```

### 5. Configurar IIS

1. **Crear Application Pool:**
   - Nombre: `AuroraLegacyPool`
   - .NET CLR Version: **v4.0** (no "No Managed Code")
   - Managed Pipeline Mode: **Integrated**
   - Identity: `ApplicationPoolIdentity` (o cuenta de servicio)

2. **Crear Sitio Web:**
   - Nombre: `Aurora File Bucket Legacy`
   - Physical Path: `C:\inetpub\wwwroot\AuroraFileBucket`
   - Binding: `http` puerto 80 (o 443 con certificado)
   - Application Pool: `AuroraLegacyPool`

3. **Permisos de carpetas:**
```cmd
icacls "C:\inetpub\wwwroot\AuroraFileBucket\App_Data" /grant "IIS AppPool\AuroraLegacyPool:(OI)(CI)F"
icacls "C:\inetpub\wwwroot\AuroraFileBucket\App_Data" /grant "IIS AppPool\AuroraLegacyPool:(OI)(CI)F"
```

### 6. Configurar `Web.config`

Edita `C:\inetpub\wwwroot\AuroraFileBucket\Web.config`:

```xml
<connectionStrings>
  <add name="AuroraDB" connectionString="Data Source=|DataDirectory|\AuroraLegacy.db;Version=3;" providerName="System.Data.SQLite" />
</connectionStrings>

<appSettings>
  <add key="JwtSecret" value="TU_CLAVE_SECRETA_AQUI_MINIMO_32_CARACTERES_PARA_HS256" />
  <add key="AccessTokenMinutes" value="20" />
  <add key="RefreshTokenHours" value="24" />
  <add key="JwtIssuer" value="aurora-file-bucket" />
  <add key="JwtAudience" value="aurora-file-bucket" />
  <add key="StagingRoot" value="D:\aurora-staging" />
  <add key="VaultsRoot" value="D:\aurora-vaults" />
  <add key="MaxChunkBytes" value="37748736" />
  <add key="BackpressurePercent" value="85" />
</appSettings>
```

### 7. Crear directorios de almacenamiento

```cmd
mkdir D:\aurora-staging
mkdir D:\aurora-vaults
mkdir D:\aurora-data

icacls D:\aurora-staging /grant "IIS AppPool\AuroraLegacyPool:(OI)(CI)F"
icacls D:\aurora-vaults /grant "IIS AppPool\AuroraLegacyPool:(OI)(CI)F"
icacls D:\aurora-data /grant "IIS AppPool\AuroraLegacyPool:(OI)(CI)F"
```

## Uso

### Acceso a la interfaz web

```
http://tu-servidor/AuroraFileBucket/
```

Usuario por defecto: `admin@aurora.local` / `admin123`

### API Endpoints

| Método | Endpoint | Descripción |
|--------|----------|-------------|
| POST | `/api/auth/login` | Login (devuelve JWT + refresh) |
| POST | `/api/auth/refresh` | Renovar tokens |
| POST | `/api/uploads/init` | Iniciar subida |
| PUT | `/api/uploads/{id}/parts/{n}` | Subir chunk |
| GET | `/api/uploads/{id}/status` | Estado |
| POST | `/api/uploads/{id}/complete` | Completar |

### Cliente de línea de comandos (compatible)

```bash
# Cliente Python
aurora-send --server http://tu-servidor --file archivo.mp4 --email admin@aurora.local --password admin123

# Cliente .NET
dotnet run --project client-dotnet -- --server http://tu-servidor --file archivo.mp4 --email admin@aurora.local --password admin123
```

## Diferencias con versión .NET Core

| Aspecto | Legacy (4.8) | Core (8.0) |
|---------|-------------|------------|
| Runtime | .NET Framework 4.8 (incluido en Windows) | .NET 8 (requiere instalación) |
| Hosting | IIS nativo | IIS + Hosting Bundle |
| Compresión | Sin Zstd nativo (requiere librería) | ZstdSharp.Port |
| Base de datos | SQLite + ADO.NET / EF6 | Microsoft.Data.Sqlite |
| JWT | Microsoft.IdentityModel.Tokens | System.IdentityModel.Tokens.Jwt |
| Configuración | Web.config | appsettings.json |
| Despliegue | xcopy / IIS Manager | dotnet publish + IIS |

## Limitaciones conocidas

1. **Zstd nativo no disponible** - La compresión se omite o requiere librería C++ compilada para .NET Framework
2. **EF6 vs EF Core** - Usa Entity Framework 6 clásico
3. **WebForms vs Minimal APIs** - Menos performante para alta concurrencia
4. **No hay Kestrel** - Solo IIS clásico

## Migración recomendada

Para nuevos despliegues, **usa la versión .NET 8** (`server-dotnet/`). Esta versión legacy es solo para entornos donde no es posible instalar .NET 8.

## Licencia

MIT - Ver LICENSE en la raíz del repositorio.