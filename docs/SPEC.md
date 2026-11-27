---
style: report
title: "Especificación Técnica: Aurora File Bucket (Transferencia de Archivos Masivos de hasta 100 GB)"
subtitle: "Arquitectura en monorepo open source con receptor ASP.NET/IIS, clientes Windows (.NET) y Linux (Python), compresión adaptativa y token rotativo"
author: "Especificación de Inicialización"
date: "Noviembre 2026"
cover: false
toc: false
font: "Calibri"
heading_font: "Calibri"
primary: "1E3A5F"
accent: "1D4ED8"
alt_row: "F4F6F9"
text: "1F2933"
header: "Aurora File Bucket · Especificación Técnica (100 GB)"
page_numbers: "auto"
---

# 1. Propósito, alcance y arquitectura general

Este documento define la especificación técnica inicial para un proyecto de **código abierto** organizado como **monorepo**, cuyo objetivo es transferir archivos individuales de hasta **100 GB** desde estaciones de trabajo o servidores hacia un servidor en la nube bajo **Windows Server con Internet Information Services (IIS) y ASP.NET**. El sistema está diseñado para operar con consumo constante de memoria RAM (< 64 MB por transferencia), proteger el ancho de banda de la red local y garantizar integridad bit a bit mediante verificación criptográfica.

![Figura 1: Arquitectura de dos partes entre clientes multiplataforma y el receptor ASP.NET/IIS en la nube.](arch_diagram.jpg){width=5.3}

La solución se divide en dos capas desacopladas que se comunican exclusivamente por HTTPS:

- **Servidor receptor en la nube (ASP.NET sobre IIS):** Único punto de entrada en Windows Server. Gestiona la autenticación con token rotativo, registra el manifiesto de bloques en base de datos, recibe fragmentos binarios por *streaming* directo a disco y ejecuta el reensamblaje, descompresión y validación final de hashes.
- **Clientes de envío multiplataforma (.NET y Python):** Dos implementaciones equivalentes bajo un mismo contrato OpenAPI: un cliente nativo en **.NET 8/9 para Windows** y un cliente en **Python 3.12+ para Linux**. El usuario designa el archivo de 100 GB y el cliente orquesta el cálculo de hashes, particionamiento, compresión, control de ancho de banda y subida reanudable.

# 2. Flujo de procesamiento y estrategia de fragmentación

Un archivo de 100 GB (102,400 MiB) no puede transmitirse en una sola petición HTTP: los proxies e IIS cortan conexiones prolongadas y el límite predeterminado de `maxAllowedContentLength` en IIS es de `30,000,000` bytes (~28.6 MB) según la [documentación de cargas en ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-11.0). Por ello, el cliente ejecuta un flujo secuencial y determinista de seis etapas:

1. **Designación y lectura por flujo:** El usuario selecciona el archivo origen. El cliente abre el archivo en modo solo lectura secuencial sin cargarlo en RAM y calcula el hash global `SHA-256` (o lo calcula de forma incremental mientras lee cada bloque para evitar una doble lectura de 100 GB en disco).
2. **Particionamiento en chunks uniformes:** El archivo se divide lógicamente en bloques de tamaño fijo. Se recomienda **16 MiB** por defecto (o **10 MiB** en enlaces inestables), lo que genera **6,400** o **10,240** partes respectivamente.
3. **Compresión adaptativa por bloque:** Cada chunk se comprime en memoria con **Zstandard (`zstd`, nivel 3)**. Si el tamaño comprimido supera el **95 %** del bloque original (común en archivos `.mp4`, `.zip`, `.7z` o respaldos cifrados), el cliente descarta la compresión para ese bloque y lo marca como `compression: "none"`, evitando desperdiciar CPU en cliente y servidor.
4. **Registro del manifiesto completo:** Antes de enviar el primer byte de datos, el cliente envía el manifiesto maestro al servidor (cantidad total de partes, tamaño original total y hash global esperado) y persiste su estado local en `SQLite`.
5. **Subida secuencial con control de tasa:** Los fragmentos se envían ordenadamente (`part_number = 1..N`) respetando un límite configurable de ancho de banda (*token bucket*). Cada petición incluye el `SHA-256` del bloque para validación inmediata en el servidor.
6. **Reensamblaje, descompresión y doble hash en IIS/ASP.NET:** Al recibir el último bloque, el receptor verifica que el manifiesto esté completo, descomprime y concatena las partes en orden estricto mediante *streaming* a disco y valida el `SHA-256` global contra el reportado al inicio.

![Figura 2: Análisis de cantidad de fragmentos según el tamaño de chunk para un archivo de 100 GiB.](chunk_analysis.png){width=5.1}

Como muestra la Figura 2, elegir **16 MiB** mantiene la transferencia en **6,400 partes**, por debajo del tope estándar de **10,000 partes** de [Amazon S3 Multipart Upload](https://docs.aws.amazon.com/AmazonS3/latest/userguide/qfacts.html) en caso de migrar el almacenamiento temporal a S3 en el futuro, y evita la sobrecarga de decenas de miles de peticiones HTTP que producirían bloques de 5 MiB (20,480 partes).

![Figura 3: Pipeline de fragmentación, verificación por checksum y reintento aislado por bloque.](chunk_pipeline.jpg){width=5.3}

![Figura 4: Diagrama de flujo integral de Aurora File Bucket desde la fragmentación local hasta la bóveda y el panel SignalR.](flowchart_e2e.png){width=6.2}

# 3. Clientes multiplataforma: .NET (Windows) y Python (Linux)

Ambos clientes implementan exactamente el mismo protocolo HTTP, formato de manifiesto y base de datos local `SQLite` para reanudación, permitiendo intercambiar equipos sin cambiar el servidor.

| Aspecto técnico | Cliente Windows (.NET 8 / 9) | Cliente Linux (Python 3.12+) |
| :--- | :--- | :--- |
| **Empaquetado y despliegue** | Binario único autocontenido (`publish -p:PublishSingleFile=true`) sin dependencias externas. | Paquete CLI (`pipx` / `venv` o binario `shiv`) ideal para servidores y cronjobs Linux. |
| **Lectura y hashing** | `FileStream` con `SequentialScan` + `IncrementalHash` (`SHA256`) y `System.Buffers.ArrayPool`. | Lectura por bloques (`memoryview`) + `hashlib.sha256()` sin copias innecesarias. |
| **Compresión Zstandard** | Librería `ZstdSharp.Port` (nativa administrada) con corte automático al 95 %. | Librería `zstandard` (bindings en C de alto rendimiento) con corte al 95 %. |
| **Cliente HTTP y reintentos** | `HttpClient` con `SocketsHttpHandler`, *token bucket* y `Polly` (*exponential backoff*). | `httpx` o `aiohttp` con limitador de tasa (*token bucket*) y reintentos con *jitter*. |
| **Estado de reanudación** | Base local `SQLite` (`Microsoft.Data.Sqlite`) con registro de cada parte confirmada. | Base local `SQLite` (`sqlite3` estándar) con el mismo esquema de tabla de progreso. |

# 4. Servidor receptor en Windows Server (IIS + ASP.NET)

Dado que el servidor en la nube opera exclusivamente sobre **Windows Server con IIS y ASP.NET**, el diseño debe evitar los cuellos de botella clásicos de IIS al procesar archivos masivos:

- **Límites en `web.config` y Kestrel:** IIS bloquea por defecto cuerpos mayores a ~28.6 MB antes de llegar a ASP.NET. Se debe configurar `<requestLimits maxAllowedContentLength="37748736" />` (36 MiB para dar margen a chunks de 32 MiB más cabeceras) en `web.config`, y replicar `MaxRequestBodySize` en Kestrel según las [opciones de configuración de Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0).
- **Streaming puro sin `IFormFile`:** El controlador receptor debe leer directamente de `HttpContext.Request.Body` hacia un `FileStream` temporal en NTFS. Usar `IFormFile` provoca que ASP.NET vuelque archivos mayores a `64 KB` en archivos temporales adicionales (`ASPNETCORE_TEMP`), duplicando la escritura en disco.
- **Mitigación de reciclaje del Application Pool (`w3wp.exe`):** Un reensamblaje y descompresión de 100 GB puede tardar varios minutos en disco. Por defecto, IIS recicla el proceso `w3wp.exe` por inactividad o cada 29 horas, lo que abortaría el ensamblaje en curso. Se debe configurar el Application Pool con `StartMode = AlwaysRunning` y `Idle Time-out = 0`, o delegar la cola de ensamblaje a un **Windows Worker Service** independiente que lea la misma carpeta de *staging* NTFS y la base de datos.
- **Compatibilidad de HTTP fragmentado y `tus.io` en IIS:** Sí es totalmente compatible usar HTTP por chunks o [tusdotnet](https://github.com/tusdotnet/tusdotnet) ([protocolo tus 1.0](https://tus.io/protocols/resumable-upload)) sobre IIS. Para evitar bloqueos `405 Method Not Allowed` en `PATCH`/`HEAD`/`DELETE`, se debe desactivar el módulo **WebDAV** de IIS en `web.config` (`<modules><remove name="WebDAVModule" /></modules>`) y habilitar dichos verbos en `requestFiltering`, además de configurar `requestLimits` al tamaño máximo de chunk (no a 100 GB).

# 5. Contrato de API y modelo de persistencia

El receptor ASP.NET expone cinco endpoints REST versión `v1` protegidos por TLS 1.3 y token rotativo:

| Método y ruta | Función principal | Carga útil y validación |
| :--- | :--- | :--- |
| `POST /v1/auth/refresh` | Rota y renueva el token de acceso sin cortar la sesión. | Recibe *refresh token* válido; devuelve nuevo JWT (TTL 15–30 min) y nuevo *refresh token*. |
| `POST /v1/uploads/init` | Crea la sesión, verifica espacio libre en disco NTFS y guarda el manifiesto esperado. | Recibe `fileName`, `totalBytes`, `chunkSize`, `totalParts`, `fileSha256`. Devuelve `uploadId`. |
| `PUT /v1/uploads/{id}/parts/{n}` | Recibe el chunk `n` por *streaming* y valida su `SHA-256` antes de confirmarlo. | Cabeceras `X-Chunk-Sha256`, `X-Compression` (`zstd` o `none`) y `X-Raw-Size`. |
| `GET /v1/uploads/{id}/status` | Lista las partes confirmadas y faltantes para reanudar tras caídas. | Devuelve `receivedParts[]`, `missingParts[]` y estado (`UPLOADING`, `ASSEMBLING`, `READY`). |
| `POST /v1/uploads/{id}/complete` | Inicia el ensamblaje asíncrono, descompresión y verificación de hash global. | Valida manifiesto completo y responde `202 Accepted` con URL de consulta de estado. |

La persistencia en el servidor (SQL Server Express, PostgreSQL o SQLite en modo WAL para despliegues simples) mantiene dos entidades maestras:

| Entidad | Columnas clave | Propósito en el ciclo de 100 GB |
| :--- | :--- | :--- |
| `upload_sessions` | `upload_id` (UUID), `file_name`, `total_bytes`, `total_parts`, `expected_sha256`, `status`, `expires_at` | Controla el estado global (`INIT` -> `READY` / `FAILED`), el hash original esperado y la expiración de sesiones abandonadas. |
| `upload_parts` | `upload_id`, `part_number` (1..N), `raw_offset`, `raw_size`, `compressed_size`, `compression`, `chunk_sha256` | Asegura que no falte ni se duplique ningún bloque, registra el método de compresión por parte y audita cada chunk antes de unirlo. |

# 6. Seguridad con token rotativo y limpieza automática

Una transferencia de 100 GB sobre un enlace residencial o corporativo limitado puede durar entre **2 y 12 horas**. Si el token de autenticación expira a mitad del proceso, la subida fallaría; por el contrario, un token estático de larga duración aumenta el riesgo ante filtraciones.

- **Inicio de sesión con OAuth 2.0 / Google (OIDC) y emisión de token rotativo:** Los usuarios se autentican mediante su cuenta de Google usando **OAuth 2.0 / OpenID Connect** con **PKCE**. El panel web usa el flujo estándar de Authorization Code; el cliente `.NET` (Windows) abre el navegador del sistema con callback local en `127.0.0.1`, y el cliente `Python` (Linux headless) soporta *Device Flow* o intercambio de código. Tras validar la identidad de Google contra una lista blanca de correos o dominio autorizado (`AllowedAdminEmails`), el servidor ASP.NET emite un **JWT efímero (15–30 minutos)** y un **Refresh Token de un solo uso con rotación automática**. Ambos clientes renuevan el token entre chunks mediante `POST /v1/auth/refresh` cuando restan menos de 3 minutos, evitando cortes en transferencias de 100 GB. **Alcance del token:** sin este token no es posible iniciar ninguna sesión (`/init`), y el mismo token autoriza consultar el estado de avance (`GET /v1/uploads/{id}/status`) y el historial de las transferencias pertenecientes a ese usuario o sus bóvedas asignadas.
- **Escritura atómica y limpieza inmediata post-ensamblaje:** Los fragmentos llegan a `D:\Staging\{upload_id}\part_{n:05d}.bin`. Al ensamblar, el servidor escribe en `archivo.assembling`, calcula el `SHA-256` global al vuelo y ejecuta `File.Move(..., overwrite: true)` hacia la bóveda final solo cuando el digest coincide bit a bit. **Inmediatamente después** del movimiento exitoso, el trabajador .NET elimina recursivamente la carpeta `D:\Staging\{upload_id}\` y todos sus chunks temporales para liberar al instante esos 100 GB sin esperar procesos nocturnos.
- **Purga de sesiones huérfanas (72 horas):** Adicionalmente, una tarea programada diaria elimina carpetas en `Staging` de transferencias abandonadas cuya inactividad supere las **72 horas** (alineado con prácticas de [expiración de sesiones reanudables](https://docs.cloud.google.com/storage/docs/resumable-uploads)) y actualiza su estado a `EXPIRED`.

# 7. Optimización de ancho de banda, recursos y alternativas

Enviar 100 GB sin controles puede saturar el enlace de subida de toda la red local o ahogar el disco y la CPU del servidor IIS. Las siguientes decisiones optimizan el sistema:

- **Control de ancho de banda (*Token Bucket*):** Tanto el cliente .NET como el cliente Python integran un parámetro obligatorio `--max-bandwidth` (ej. `50 Mbps` o `5 MB/s`) mediante un algoritmo *token bucket* en el flujo de salida HTTP. Así, la transferencia aprovecha el canal disponible sin degradar videollamadas ni otros servicios de la red.
- **Subida secuencial frente a concurrencia limitada:** Subir de 1 en 1 en forma estrictamente secuencial protege la red y simplifica el orden, pero en enlaces de alta latencia (ej. > 80 ms hacia la nube) el protocolo TCP tarda en alcanzar su velocidad máxima por petición. Como mejora opcional configurable (`--concurrency 2` o `3`), permitir 2–3 hilos paralelos aumenta el rendimiento sin exceder el tope global de ancho de banda compartido por el *token bucket*.
- **Cero doble lectura en disco:** Si el cliente calcula el `SHA-256` global al mismo tiempo que lee los bloques de 16 MiB para comprimirlos y subirlos (enviando `expected_sha256` en `/complete` en lugar de `/init`), se evita leer 100 GB dos veces del disco duro local, reduciendo a la mitad el tiempo previo al primer envío.
- **Parámetros configurables de fragmentación, paralelismo y límites:** Tanto los clientes (.NET/Python) como el servidor IIS exponen límites ajustables mediante archivo de configuración o CLI, partiendo de valores estándar seguros:

| Parámetro | Valor estándar | Rango permitido | Propósito operativo |
| :--- | :---: | :---: | :--- |
| `chunkSizeMiB` | `16 MiB` | `4 – 32 MiB` | Ajusta el tamaño del bloque según estabilidad de red y tope de `web.config`. |
| `maxConcurrency` | `3` hilos | `1 – 6` hilos | Define cuántos chunks se envían en paralelo sin saturar el disco en IIS. |
| `maxBandwidthMbps` | `50 Mbps` | `1 – 1000 Mbps` | Tope global compartido (*token bucket*) entre todos los hilos paralelos. |
| `vaultQuotaGB` | `500 GB` | Configurable | Límite máximo de almacenamiento por bóveda y por transferencia individual. |

- **¿Es esta la forma más óptima?** Para un servidor propio **exclusivamente en Windows Server + IIS + ASP.NET**, sí: el streaming por chunks de 16 MiB con compresión adaptativa y *token bucket* evita la saturación de RAM, supera los límites de IIS y tolera cortes de red. La única alternativa aún más rápida en entornos híbridos sería usar **almacenamiento de objetos directo** (Azure Blob Storage o S3 con URLs prefirmadas) si en el futuro se permite que el tráfico pesado no pase por el servidor IIS.

# 8. Objeciones técnicas, cuellos de botella y mejoras recomendadas

Más allá del diseño base, existen cinco puntos críticos donde una implementación ingenua fallaría o degradaría severamente el rendimiento al escalar a 100 GB:

- **Objeción 1 — Comprimir a ciegas satura la CPU sin reducir tráfico:** Si los archivos de 100 GB son videos (`.mp4`, `.mkv`), respaldos ya comprimidos (`.zip`, `.tar.gz`) o volúmenes cifrados, comprimir cada chunk consume 100 % de la CPU del cliente y del servidor para ganar menos del 1 %. *Mejora:* ejecutar una prueba de compresibilidad sobre los primeros 2 MB del archivo antes de iniciar; si la reducción es menor al 8 %, desactivar la compresión globalmente para toda la sesión.
- **Objeción 2 — Reensamblar en disco triplica el I/O del servidor IIS:** Escribir 100 GB en bloques temporales, leerlos de nuevo y escribir otros 100 GB en el archivo final genera **300 GB de tráfico en disco**, lo que puede ahogar el almacenamiento de la máquina virtual Windows. *Mejora:* cuando los bloques no requieran compresión, escribir cada chunk directamente en su desplazamiento exacto (`Position = raw_offset`) dentro de un archivo preasignado (`FileStream.SetLength(totalBytes)`), eliminando la copia de reensamblaje.
- **Objeción 3 — Subida estrictamente 1 a 1 subutiliza enlaces con latencia:** Enviar un chunk tras otro en un solo hilo HTTP deja la red ociosa esperando la respuesta del servidor en cada bloque. *Mejora:* combinar el limitador *token bucket* global (para no comerse el ancho de banda de la oficina) con un pool pequeño de 2 a 4 envíos concurrentes.
- **Objeción 4 — Calcular el hash global antes de subir duplica el tiempo de lectura:** Leer 100 GB solo para sacar el `SHA-256` inicial retrasa el primer byte enviado. *Mejora:* calcular el `SHA-256` incrementalmente mientras se leen los chunks para subirlos y enviar el hash global esperado al cierre en `POST /complete`.
- **Objeción 5 — Falta de contrapresión (backpressure) en el servidor:** Si varios clientes suben 100 GB al mismo tiempo, el disco del servidor IIS puede saturarse. *Mejora:* implementar control de flujo en ASP.NET devolviendo `429 Too Many Requests` con cabecera `Retry-After` cuando la cola de escritura en disco o el uso de almacenamiento supere el 85 %.

# 9. Panel de administración en tiempo real y organización por bóvedas

Para dar visibilidad operativa sobre lo que está llegando al servidor IIS sin revisar logs manualmente, el receptor ASP.NET incorpora una **consola web de administración** y un esquema de almacenamiento aislado por **bóvedas (*vaults*)**:

- **Monitoreo en tiempo real con SignalR:** El servidor expone un dashboard ligero (Razor Pages o Blazor) conectado mediante **ASP.NET Core SignalR** (WebSockets habilitados en IIS). Cada vez que un cliente confirma un chunk en `PUT /parts/{n}` o cambia de estado en `/complete`, el servidor emite el evento al panel mostrando barra de progreso (`partes recibidas / total`), tasa de transferencia (MB/s), cliente origen (Windows `.NET` o Linux `Python`), estado de verificación `SHA-256` y acciones manuales para cancelar o purgar sesiones huérfanas.
- **Arquitectura de bóvedas aisladas (*Vaults*):** En lugar de mezclar archivos de 100 GB en un solo directorio, cada proyecto, departamento o cliente se asigna a una **bóveda lógica** (`vault_id`, `slug`, cuota máxima en GB y ruta base en NTFS, p. ej. `D:\Vaults\{vault_slug}\`).
- **Aislamiento de permisos y separación *Staging* vs. Bóveda:** Cada token rotativo queda restringido a una o más bóvedas específicas (principio de mínimo privilegio). Los chunks incompletos siempre residen en `D:\Staging\{upload_id}\` y el archivo solo entra a `D:\Vaults\{vault_slug}\` mediante movimiento atómico una vez verificado el `SHA-256` global; así, las bóvedas finales jamás contienen archivos corruptos o a medio subir.

- **Notificaciones por correo electrónico al completar:** Una vez que el trabajador .NET valida el `SHA-256` global y mueve el archivo a la bóveda final (o si la sesión falla definitivamente), dispara una notificación por correo electrónico (vía SMTP o proveedor configurado en `appsettings.json`) al correo verificado en el perfil de Google del usuario que inició la carga y a los observadores configurados en la bóveda. El correo incluye el nombre del archivo, bóveda de destino, tamaño final, tiempo total, `SHA-256` verificado y enlace al panel de administración.
- **Concurrencia y tamaño de chunk adaptativos en tiempo real:** Además de los límites configurables iniciales, el cliente puede ajustar dinámicamente el tamaño de los siguientes chunks (dentro del rango permitido) y escalar o reducir la concurrencia (1–6 hilos) según la latencia y tasa de éxito observadas, respetando siempre las señales `429 Retry-After` (*backpressure*) del servidor IIS.

- **Notificaciones por correo electrónico y Telegram al completar:** Una vez que el trabajador .NET valida el `SHA-256` global y mueve el archivo a la bóveda final (o si la sesión falla definitivamente), dispara notificaciones multicanal configurables en `appsettings.json` y por bóveda:
  - **Correo electrónico (SMTP / proveedor transaccional):** enviado al correo verificado del perfil de Google del usuario que inició la carga y a los observadores de la bóveda, detallando archivo, bóveda, tamaño final, duración, hash `SHA-256` y enlace al panel.
  - **Telegram (Bot API):** mensaje automático mediante `sendMessage` hacia un `ChatId` o grupo configurado (usando `BotToken` en variables de entorno o secretos de IIS) con el resumen instantáneo de finalización o alerta de fallo.
- **Concurrencia y tamaño de chunk adaptativos en tiempo real:** Además de los límites configurables iniciales, el cliente puede ajustar dinámicamente el tamaño de los siguientes chunks (dentro del rango permitido) y escalar o reducir la concurrencia (1–6 hilos) según la latencia y tasa de éxito observadas, respetando siempre las señales `429 Retry-After` (*backpressure*) del servidor IIS.

# 10. Estructura del monorepo y proyecto Open Source

Al ser un proyecto **abierto a la comunidad**, el repositorio se inicializa con esta especificación como documento base (`docs/SPEC.md`), licencia permisiva y validación automática multiplataforma:

```text
aurora-file-bucket/
├── LICENSE               # Licencia Apache-2.0 o MIT
├── README.md             # Guía rápida y arquitectura
├── CONTRIBUTING.md       # Guía de PRs y pruebas
├── SECURITY.md           # Política de seguridad y tokens
├── docs/SPEC.md          # Especificación técnica viva
├── contracts/openapi.yaml# Contrato compartido v1 (JSON/HTTP)
├── server-dotnet/        # Receptor ASP.NET + web.config IIS
├── client-dotnet/        # Cliente CLI .NET (Windows)
├── client-python/        # Cliente CLI Python 3.12+ (Linux)
├── landing-page/         # Landing page autocontenida (1 archivo HTML, CSS embebido) para GitHub Pages
└── .github/workflows/    # CI en Windows y Linux
```

# 11. Fuentes y referencias técnicas

1. [Upload files in ASP.NET Core — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-11.0) (límites de tamaño, streaming directo y `MemoryBufferThreshold`).
2. [Configure options for the ASP.NET Core Kestrel web server — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0) (`MaxRequestBodySize` y tasas mínimas de transmisión).
3. [tusdotnet: .NET server implementation of the Tus protocol — GitHub](https://github.com/tusdotnet/tusdotnet) (referencia de middleware reanudable para ASP.NET Core).
4. [Resumable upload protocol 1.0.x — tus.io](https://tus.io/protocols/resumable-upload) (estándar abierto de cargas reanudables sobre HTTP).
5. [Amazon S3 multipart upload limits — AWS Documentation](https://docs.aws.amazon.com/AmazonS3/latest/userguide/qfacts.html) (referencia de límite de 10,000 partes y tamaño máximo de objeto).
6. [Resumable uploads | Cloud Storage — Google Cloud Documentation](https://docs.cloud.google.com/storage/docs/resumable-uploads) (referencia de ciclo de vida y expiración de sesiones).
