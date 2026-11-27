# Roadmap

Estado de la línea de desarrollo de Aurora File Bucket. La especificación de producto vive en
`docs/SPEC.md`; el contrato de comportamiento, en `docs/PROTOCOL.md`.

## v0.1.0 — primera versión (en curso)

Un corte vertical completo del protocolo: receptor, dos clientes y las garantías de integridad.

- Receptor ASP.NET Core sobre IIS: streaming de chunks a NTFS, hash SHA-256 por chunk,
  ensamblaje en segundo plano con verificación del hash global, movimiento atómico a la
  bóveda, limpieza inmediata del staging, purga de sesiones huérfanas, contrapresión `429`.
- API v1 completa: `login`, `refresh`, `uploads/init`, `parts/{n}`, `status`, `complete`.
- Token rotativo: JWT efímero de 20 min + refresh token de un solo uso con rotación.
- Aislamiento por bóvedas con cuota y mínimo privilegio.
- Cliente .NET 8/9 (Windows) y cliente Python 3.12+ (Linux): mismo protocolo, mismo esquema
  SQLite de reanudación, token bucket global, reintentos con backoff, hash incremental.
- Tests unitarios en ambas plataformas + escenario end-to-end que sube un archivo real y
  comprueba su SHA-256 en la bóveda.
- Landing page publicada en GitHub Pages.

### Diferencias declaradas respecto de `docs/SPEC.md`

Estas piezas del spec están **aplazadas a v0.2**; la v0.1 deja el punto de extensión listo:

| Apartado del spec | Estado en v0.1 |
| :--- | :--- |
| §6 — OAuth 2.0 / Google (OIDC con PKCE) | Sustituido por `POST /v1/auth/login` con usuarios locales. La forma de la respuesta ya es la definitiva, así que sustituirla por el callback de Google no rompe clientes. |
| §9 — Panel en tiempo real con SignalR | Emisión de eventos en el servidor preparada; la consola web y el panel de SignalR quedan para v0.2. |
| §9 — Notificaciones por correo y Telegram | Interfaz de notificación y disparadores en el ensamblaje; falta el panel de observadores por bóveda y la configuración fina. |
| §9 — Tamaño de chunk y concurrencia adaptativos | El cliente fija los valores por configuración; la adaptación dinámica según latencia queda para v0.2. |
| §8, Objeción 2 — Escritura directa en `raw_offset` | v0.1 ensambla en una sola pasada secuencial (≈200 GB de tráfico: lectura de staging + escritura del destino). La escritura preasignada se evalúa en v0.2. |

## v0.2 — identidad real y operación

- **Google OIDC**: Authorization Code + PKCE para el panel web, callback local `127.0.0.1`
  para el cliente .NET y Device Flow para el cliente Python headless. Lista blanca de
  correos y dominio autorizado (`AllowedAdminEmails`).
- **Panel SignalR**: dashboard con barra de progreso, tasa de transferencia, cliente origen,
  verificación SHA-256 y acciones manuales de cancelar o purgar sesiones.
- **Notificaciones**: correo SMTP al correo verificado y a los observadores de la bóveda;
  Telegram con `sendMessage` a un `ChatId` o grupo configurado.
- **Adaptación dinámica**: ajuste del tamaño de chunk y de la concurrencia dentro de los
  rangos permitidos, respetando `429 Retry-After`.
- **Consola de administración**: página de estado con el historial de transferencias por
  usuario y bóveda.

## v0.3 — escala y formatos

- **Almacenamiento de objetos directo**: Azure Blob Storage o S3 con URLs prefirmadas para
  sacar el tráfico pesado del servidor IIS (la alternativa considerada en §7 del spec).
- **Compatibilidad tus.io**: middleware `tusdotnet` como alternativa al protocolo propio,
  manteniendo el mismo manifiesto y las mismas garantías de hash.
- **Escritura preasignada** en `raw_offset` para cutting de chunks sin compresión.
- **Réplica y verificación cruzada** entre bóvedas.

## Decisiones abiertas

- ¿SQLite en modo WAL o PostgreSQL como almacén por defecto del receptor? Hoy el servidor
  usa SQLite por simplicidad de despliegue; la elección se documentará cuando haya datos de carga real.
- ¿El hash global se calcula en el cliente o se acepta el hash del servidor? Hoy el cliente
  lo envía y el servidor lo recalcula: la verificación es bilateral.
