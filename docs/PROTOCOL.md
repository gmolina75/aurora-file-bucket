# Protocolo Aurora File Bucket v1

> Documento normativo del protocolo. `contracts/openapi.yaml` describe las mismas
> rutas en OpenAPI 3.1; cuando haya cualquier discrepancia, **manda este documento**
> para el comportamiento en tiempo de ejecución y `openapi.yaml` para la forma JSON.

Basado en `docs/SPEC.md` (sección 5, "Contrato de API"). Versión de protocolo: **1.0.0**.

---

## 0. Reglas globales

| Aspecto | Valor normativo |
| :--- | :--- |
| Base URL | `https://<host>/v1` (TLS 1.3 obligatorio en producción) |
| Codificación | Todo el JSON en UTF-8, `Content-Type: application/json; charset=utf-8` |
| Cuerpos de chunk | `application/octet-stream`, `Content-Length` obligatorio (sin `Transfer-Encoding: chunked`) |
| Fechas | RFC 3339 UTC, formato `2026-11-27T14:03:11Z` |
| Hashes | SHA-256 en hexadecimal minúsculas, 64 caracteres, sin prefijo `0x` |
| IDs | UUID v4 en formato con guiones, minúsculas |
| Nombres de archivo | UTF-8, sanitizados por el servidor (ver §2.4) |

Toda respuesta de error usa **exactamente** esta forma:

```json
{
  "error": {
    "code": "chunk_hash_mismatch",
    "message": "Texto legible para humanos.",
    "details": { "cualquier": "dato opcional" }
  }
}
```

`details` es opcional y **debe** ser un objeto JSON cuando aparece.

### 0.1 Códigos de estado HTTP

| Código | Cuándo se usa |
| :--- | :--- |
| `200` | Operación exitosa de lectura o de confirmación de chunk. |
| `201` | Se creó una sesión de subida (`/uploads/init`). |
| `202` | Ensamblaje encolado (`/uploads/{id}/complete`). |
| `400` | Petición malformada: manifiesto inválido, JSON inválido, cabeceras ausentes o hash de chunk incorrecto. |
| `401` | Token ausente, inválido o expirado. |
| `403` | El token no tiene permiso sobre la bóveda solicitada. |
| `404` | `uploadId` inexistente o vencido. |
| `409` | Conflicto de estado: chunk ya confirmado con otro contenido, o ensamblaje solicitado con partes faltantes. |
| `413` | Cuota de bóveda excedida o disco sin espacio. |
| `429` | Contrapresión de disco activa. **Siempre** incluye cabecera `Retry-After` en segundos. |
| `500` | Error interno. El cuerpo no filtra detalles de implementación. |

---

## 1. Autenticación

### 1.1 Modelo

- **JWT de acceso** (HS256, symmetric): TTL por defecto **20 minutos**
  (`Auth:AccessTokenMinutes`, rango permitido 15–30 según SPEC §6).
  Claims: `sub` (email), `name`, `vaults` (array de slugs de bóveda permitidos),
  `iat`, `exp`, `jti`.
- **Refresh token**: opaco, 32 bytes aleatorios en base64url. Se guarda **solo su
  hash SHA-256** en la base de datos. TTL por defecto **24 h**
  (`Auth:RefreshTokenHours`). **De un solo uso**: al canjearlo se revoca y se
  emite uno nuevo.

```
Authorization: Bearer <accessToken>
```

### 1.2 `POST /v1/auth/login`

Emisión inicial de tokens. En v0.1 valida contra los usuarios configurados en
`Auth:Users` (modo desarrollo). **v0.2** sustituye este endpoint por el callback
de Google OIDC (Authorization Code + PKCE); la forma de la respuesta **no cambia**.

Request:

```json
{ "email": "user@example.com", "password": "secreto" }
```

Response `200`:

```json
{
  "tokenType": "Bearer",
  "accessToken": "<jwt>",
  "refreshToken": "<opaque>",
  "expiresIn": 1200,
  "user": { "email": "user@example.com", "displayName": "Nombre legible" },
  "vaults": ["default"]
}
```

| Error | Código | `error.code` |
| :--- | :--- | :--- |
| Credenciales inválidas | `401` | `invalid_credentials` |
| Email no autorizado | `403` | `email_not_allowed` |

### 1.3 `POST /v1/auth/refresh`

Rota el par de tokens sin cortar la transferencia en curso.

Request:

```json
{ "refreshToken": "<opaque>" }
```

Response `200`: idéntica a §1.2 (nuevo `accessToken`, nuevo `refreshToken`,
nuevo `expiresIn`).

| Error | Código | `error.code` |
| :--- | :--- | :--- |
| Refresh token inválido, expirado, revocado o ya canjeado | `401` | `invalid_refresh_token` |

**Regla de canje único:** si llega un refresh token ya revocado, el servidor
revoca **todos** los refresh tokens de ese usuario (detección de reutilización) y
responde `401`.

### 1.4 Regla de renovación del cliente

El cliente renueva el token **cuando faltan menos de 3 minutos** para que expire
(`expiresIn - (ahora - emisión) < 180 s`), y ante cualquier `401` reintenta **una
sola vez** tras renovar. Nunca espera pasivamente a que expire.

---

## 2. Ciclo de vida de la subida

### 2.1 `POST /v1/uploads/init`

Crea la sesión y registra el manifiesto esperado. **No** se envía ningún byte de
datos todavía.

Cabeceras obligatorias:

| Cabecera | Valor |
| :--- | :--- |
| `Authorization` | `Bearer <accessToken>` |
| `X-Vault-Slug` | Slug de la bóveda destino. Opcional: si se omite, se usa la primera bóveda permitida del token. |
| `X-Client-Id` | UUID de la instancia cliente. Informativo (panel). |
| `X-Client-Platform` | `windows-dotnet` \| `linux-python`. Informativo (panel). |

Request:

```json
{
  "fileName": "pelicula.mp4",
  "totalBytes": 107374182400,
  "chunkSize": 16777216,
  "totalParts": 6400,
  "fileSha256": null,
  "compression": "auto"
}
```

| Campo | Tipo | Regla |
| :--- | :--- | :--- |
| `fileName` | string | Obligatorio, 1–255 caracteres. No puede contener ruta ni separadores. |
| `totalBytes` | integer | Obligatorio, `> 0`. |
| `chunkSize` | integer | Obligatorio. Bytes. Rango `[4 MiB, 32 MiB]` (configurable). |
| `totalParts` | integer | Obligatorio. Debe ser exactamente `ceil(totalBytes / chunkSize)`. |
| `fileSha256` | string \| null | Opcional. SHA-256 del archivo **original** (bytes sin comprimir). `null` cuando el cliente lo calcula de forma incremental durante la subida. |
| `compression` | string | `auto` (zstd con corte adaptativo) o `none`. |

Response `201`:

```json
{
  "uploadId": "3f9a1c6e-...-b21",
  "status": "INIT",
  "vaultSlug": "default",
  "fileName": "pelicula.mp4",
  "totalBytes": 107374182400,
  "chunkSize": 16777216,
  "totalParts": 6400,
  "receivedParts": [],
  "missingParts": [1, 2, 3, 4, 5, 6],
  "expiresAt": "2026-11-30T14:03:11Z"
}
```

`missingParts` se devuelve **truncado** a los primeros 100 números; el cliente
usa `GET /status` para el conjunto completo.

| Error | Código | `error.code` |
| :--- | :--- | :--- |
| Manifiesto inconsistente o fuera de rango | `400` | `invalid_manifest` |
| Bóveda no permitida para el token | `403` | `vault_not_allowed` |
| Cuota de bóveda o espacio insuficiente | `413` | `quota_exceeded` |
| Contrapresión de disco | `429` | `backpressure` |

### 2.2 `PUT /v1/uploads/{uploadId}/parts/{partNumber}`

Recibe **un** chunk. El cuerpo son los bytes **tal como se transfieren** (comprimidos
si `X-Compression: zstd`).

Cabeceras obligatorias:

| Cabecera | Valor | Nota |
| :--- | :--- | :--- |
| `Authorization` | `Bearer <accessToken>` | |
| `Content-Type` | `application/octet-stream` | |
| `Content-Length` | entero | Obligatorio: sin `chunked`. |
| `X-Chunk-Sha256` | 64 hex | SHA-256 **del cuerpo recibido** (bytes comprimidos si los hay). |
| `X-Compression` | `zstd` \| `none` | Método con el que se comprimió el chunk. |
| `X-Raw-Size` | entero | Tamaño del chunk **descomprimido**. |
| `X-Raw-Sha256` | 64 hex | SHA-256 del chunk descomprimido. Opcional pero recomendado. |
| `X-Client-Id` | UUID | Informativo. |
| `X-Client-Platform` | string | Informativo. |

`partNumber` es 1-based y va de `1` a `totalParts`.

Response `200`:

```json
{
  "partNumber": 1,
  "compressedSize": 8388608,
  "rawSize": 16777216,
  "sha256": "9f86d0...",
  "compression": "zstd",
  "receivedParts": 1,
  "totalParts": 6400,
  "status": "UPLOADING"
}
```

**Idempotencia:** reenviar la misma parte con el mismo hash devuelve `200` y
sobrescribe el fichero. Reenviarla con un hash distinto devuelve `409`.

| Error | Código | `error.code` |
| :--- | :--- | :--- |
| Hash del cuerpo ≠ `X-Chunk-Sha256` | `400` | `chunk_hash_mismatch` |
| Parte ya confirmada con otro contenido | `409` | `part_conflict` |
| Cabecera obligatoria ausente o malformada | `400` | `invalid_request` |
| `uploadId` inexistente | `404` | `not_found` |
| Contrapresión de disco (usa espacio > 85 %) | `429` | `backpressure` + `Retry-After` |

### 2.3 `GET /v1/uploads/{uploadId}/status`

Response `200`:

```json
{
  "uploadId": "3f9a1c6e-...-b21",
  "status": "UPLOADING",
  "vaultSlug": "default",
  "fileName": "pelicula.mp4",
  "totalBytes": 107374182400,
  "chunkSize": 16777216,
  "totalParts": 6400,
  "receivedParts": [1, 2, 3],
  "missingParts": [4, 5],
  "bytesReceived": 50331648,
  "startedAt": "2026-11-27T14:03:11Z",
  "expiresAt": "2026-11-30T14:03:11Z",
  "completedAt": null,
  "vaultPath": null,
  "finalSha256": null,
  "error": null
}
```

`status` es uno de: `INIT`, `UPLOADING`, `ASSEMBLING`, `READY`, `FAILED`, `EXPIRED`.

El **servidor es la autoridad** sobre qué partes existen: tras una caída, el
cliente reconstruye su estado a partir de esta respuesta, no de su base local.

### 2.4 `POST /v1/uploads/{uploadId}/complete`

Encola el ensamblaje. No es bloqueante.

Request:

```json
{ "fileSha256": "3a7bd3e2...", "notify": true }
```

- `fileSha256`: SHA-256 esperado del archivo **original**. Si el cliente lo calculó
  de forma incremental durante la subida, lo envía **aquí** (y lo dejó en `null`
  en `/init`). Si viene informado, **prevalece** sobre el valor de `/init`.
- `notify`: si es `false`, no se envían notificaciones de correo/Telegram.

Response `202`:

```json
{
  "uploadId": "3f9a1c6e-...-b21",
  "status": "ASSEMBLING",
  "statusUrl": "/v1/uploads/3f9a1c6e-...-b21/status"
}
```

| Error | Código | `error.code` |
| :--- | :--- | :--- |
| Faltan partes por subir | `409` | `incomplete_upload` (incluye `missingCount` y una muestra en `details`) |
| Manifiesto inconsistente | `400` | `invalid_manifest` |

### 2.5 Algoritmo de ensamblaje (servidor)

Ejecutado por un trabajador en segundo plano, no dentro de la petición:

1. Cargar sesión y partes confirmadas. Verificar que están **todas**.
2. Destino final: `{VaultPath}\{fileName}`. Si ya existe, se resuelve como
   `{fileName}.{uploadId}` (antes de la extensión) y se registra en `vaultPath`.
3. Escribir primero en `{VaultPath}\{fileName}.assembling` y, en el mismo bucle,
   calcular el SHA-256 **del flujo descomprimido**:
   - Por cada parte en orden: leer el fichero de staging; si `X-Compression: zstd`,
   descomprimir en memoria (el chunk ocupa ≤ 32 MiB → RAM por transferencia
   < 64 MiB, ver SPEC §1); si no, escribir los bytes tal cual.
   - Alimentar los bytes al hash incremental y escribir en el destino.
4. Al terminar, comparar el digest con el esperado (`fileSha256` de `/complete`, o
   el de `/init` si el cliente no lo actualizó):
   - **Coincide** → `File.Move(temp, final, overwrite: true)` (atómico dentro del
     mismo volumen), se borra recursivamente `Staging\{uploadId}`, estado `READY`.
   - **No coincide** → se borra el fichero temporal, estado `FAILED`,
     `error.code = global_hash_mismatch`.
5. Actualizar `finalSha256`, `completedAt` y `vaultPath` en la base de datos.
6. Disparar notificaciones configuradas (correo y/o Telegram) si `notify` era
   `true`.

### 2.6 Limpieza y expiración

- **Inmediata:** tras un ensamblaje correcto se borra recursivamente la carpeta de
  staging, liberando el 100 GB sin esperar a procesos nocturnos (SPEC §6).
- **Purga de huérfanos:** tarea programada que elimina carpetas de `Staging` sin
  actividad superior a `OrphanSessionHours` (72 h por defecto) y marca las
  sesiones como `EXPIRED`.

---

## 3. Almacenamiento en el servidor

### 3.1 Tablas

```sql
CREATE TABLE upload_sessions (
  upload_id       TEXT PRIMARY KEY,
  vault_slug     TEXT NOT NULL,
  file_name      TEXT NOT NULL,
  total_bytes    INTEGER NOT NULL,
  chunk_size     INTEGER NOT NULL,
  total_parts    INTEGER NOT NULL,
  expected_sha256 TEXT,
  status         TEXT NOT NULL,
  client_id      TEXT,
  client_platform TEXT,
  vault_path     TEXT,
  final_sha256   TEXT,
  error_code     TEXT,
  error_message  TEXT,
  started_at     TEXT NOT NULL,
  expires_at     TEXT NOT NULL,
  completed_at   TEXT
);

CREATE TABLE upload_parts (
  upload_id      TEXT NOT NULL,
  part_number    INTEGER NOT NULL,
  raw_offset     INTEGER NOT NULL,
  raw_size       INTEGER NOT NULL,
  compressed_size INTEGER NOT NULL,
  compression    TEXT NOT NULL,
  chunk_sha256   TEXT NOT NULL,
  raw_sha256     TEXT,
  confirmed_at   TEXT NOT NULL,
  PRIMARY KEY (upload_id, part_number)
);

CREATE TABLE refresh_tokens (
  token_hash    TEXT PRIMARY KEY,
  user_email    TEXT NOT NULL,
  expires_at    TEXT NOT NULL,
  revoked_at    TEXT,
  replaced_by   TEXT
);
```

`raw_offset` = `(part_number - 1) * chunk_size` (desplazamiento del chunk dentro
del archivo original). Permite la escritura directa en su posición final
(optimización de SPEC §8, Objeción 2).

### 3.2 Rutas en disco

```
{StagingRoot}\{uploadId}\part_{partNumber:D5}.bin
{VaultPath}\{fileName}
{VaultPath}\{fileName}.assembling     (temporal durante el ensamblaje)
```

Los chunks incompletos **nunca** están dentro de una bóveda final.

---

## 4. Contrato entre clientes

Los dos clientes implementan **exactamente** este protocolo y comparten el mismo
esquema de base de datos local de reanudación.

### 4.1 Tabla de estado local (SQLite)

```sql
CREATE TABLE upload_progress (
  file_path   TEXT NOT NULL,
  file_size   INTEGER NOT NULL,
  file_mtime  REAL NOT NULL,
  server_url  TEXT NOT NULL,
  vault_slug  TEXT NOT NULL,
  upload_id   TEXT NOT NULL,
  chunk_size  INTEGER NOT NULL,
  total_parts INTEGER NOT NULL,
  file_sha256 TEXT,
  state       TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  PRIMARY KEY (file_path, file_size, server_url, vault_slug)
);

CREATE TABLE part_state (
  upload_id      TEXT NOT NULL,
  part_number    INTEGER NOT NULL,
  compression    TEXT,
  compressed_size INTEGER,
  raw_size       INTEGER,
  sha256         TEXT,
  uploaded_at    TEXT NOT NULL,
  PRIMARY KEY (upload_id, part_number)
);
```

### 4.2 Reanudación

1. Al arrancar, el cliente busca en `upload_progress` la fila que case por
   `(file_path, file_size, server_url, vault_slug)`.
2. Si existe y `file_mtime` coincide, llama a `GET /v1/uploads/{id}/status` y
   **adopta `receivedParts` del servidor** como verdad; el estado local solo sirve
   de hint.
3. Si no existe o el `file_mtime` cambió, se inicia una sesión nueva.

### 4.3 Fragmentación, compresión y ancho de banda

| Parámetro | Valor por defecto | Rango | Flag (ambos clientes) |
| :--- | :---: | :--- | :--- |
| `chunkSize` | 16 MiB | 4–32 MiB | `--chunk-size-mib` |
| `maxConcurrency` | 3 | 1–6 | `--concurrency` |
| `maxBandwidthMbps` | 50 | 1–1000 | `--max-bandwidth-mbps` |
| Compresión | zstd nivel 3 | — | `--no-compress` desactiva |

- **Corte adaptativo:** si el tamaño comprimido de un chunk supera el **95 %** del
  original, ese chunk se envía con `X-Compression: none` (SPEC §2, paso 3).
- **Prueba de compresibilidad:** antes de transferir, se comprimen los primeros
  **2 MB**; si la reducción es menor al 8 %, la compresión se desactiva para toda
  la sesión (SPEC §8, Objeción 1).
- **Token bucket:** limitador **global compartido** por todos los hilos, en
  bytes/s = `Mbps * 1_000_000 / 8`. Capacidad de cubo = 1 s de ancho de banda.
- **Cero doble lectura:** el SHA-256 global se calcula de forma incremental
  mientras se leen los chunks; el digest final se envía en `/complete`
  (SPEC §8, Objeción 4).

### 4.4 Reintentos

| Situación | Comportamiento |
| :--- | :--- |
| `429` con `Retry-After` | Se espera **exactamente** ese número de segundos y se reintenta (sin límite de reintentos de backpressure, salvo `--max-retries`). |
| `5xx`, error de red, timeout | Backoff exponencial con jitter: `1 s, 2 s, 4 s, 8 s, 16 s` (máx. 5 intentos por defecto). |
| `400 chunk_hash_mismatch` | **No** se reintenta: es un error de datos, se aborta. |
| `401` | Se renueva el token **una** vez y se reintenta. |
| `409 part_conflict` | No se reintenta: el servidor tiene otra versión de esa parte. |

---

## 5. Cabeceras comunes

| Cabecera | Enviada por | Significado |
| :--- | :--- | :--- |
| `Authorization` | Cliente | `Bearer <accessToken>` |
| `X-Vault-Slug` | Cliente | Bóveda destino en `/init` |
| `X-Client-Id` | Cliente | UUID de instancia (panel) |
| `X-Client-Platform` | Cliente | `windows-dotnet` / `linux-python` (panel) |
| `X-Chunk-Sha256` | Cliente | Hash del cuerpo del chunk |
| `X-Raw-Sha256` | Cliente | Hash del chunk descomprimido |
| `X-Compression` | Cliente | `zstd` / `none` |
| `X-Raw-Size` | Cliente | Tamaño descomprimido |
| `Retry-After` | Servidor | Segundos a esperar ante `429` |
