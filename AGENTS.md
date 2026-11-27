# AGENTS.md

Aurora File Bucket — open-source monorepo for resumable, integrity-verified transfer of single files up to **100 GB** from Windows (.NET) and Linux (Python) workstations to a **Windows Server + IIS + ASP.NET** receiver. Full design in `docs/SPEC.md`.

## Non-negotiable contracts

Read these before changing anything; they are the source of truth, in this order:

1. `docs/PROTOCOL.md` — **normative** wire protocol (headers, JSON shapes, status codes, retry rules).
2. `contracts/openapi.yaml` — machine-readable view of the same API.
3. `docs/SPEC.md` — product and architecture specification.

`client-dotnet/` and `client-python/` are two implementations of **one** protocol. A change to chunk headers, hashing, manifest fields or the local resume schema must land in **both** clients in the same PR, or CI breaks.

## Setup commands

- .NET SDK 8.0+ (targets `net8.0`): `dotnet restore aurora-file-bucket.sln`
- .NET build: `dotnet build aurora-file-bucket.sln`
- .NET tests: `dotnet test aurora-file-bucket.sln`
- Python 3.12+: `python -m pip install -e ./client-python` then `python -m pip install pytest`
- Python tests: `python -m pytest client-python/tests`
- End-to-end (server + real client + real file): `python scripts/e2e.py`
- Run the server locally: `dotnet run --project server-dotnet/AuroraFileBucket.Server.csproj`

## Project layout

- `server-dotnet/` — ASP.NET Core receiver: streaming chunk ingest, Zstandard decompression, SHA-256 verification, vault isolation, background assembly worker, orphan purge. Published to IIS via `web.config`.
- `client-dotnet/` — .NET 8 CLI sender for Windows. Single-file publishable.
- `client-python/` — Python 3.12+ CLI sender for Linux (`aurora_sender` package, entry point `aurora-send`).
- `contracts/` — `openapi.yaml`, the shared API contract.
- `docs/` — `SPEC.md` (design), `PROTOCOL.md` (normative behaviour), `assets/`.
- `landing-page/` — single-file static landing page published to GitHub Pages.
- `scripts/` — `e2e.py` and other repo automation.
- `.github/workflows/` — `ci.yml` (Windows + Linux matrix), `pages.yml` (GitHub Pages deploy).

## Code style

- C#: file-scoped namespaces, `async`/`await` end to end, nullable reference types enabled, `CancellationToken` threaded through every I/O call, no `async void`.
- C#: use `HttpContext.Request.Body` streaming directly to a `FileStream` — never `IFormFile`, never `MemoryStream` for chunk payloads.
- Python: type hints on public functions, `pathlib` over `os.path`, `httpx` for HTTP, stdlib `sqlite3` for state, no global mutable state.
- Both clients: identical CLI flag names, identical config keys, identical SQLite schema.
- Keep chunk payloads out of RAM beyond the chunk itself (≤ 32 MiB); the transfer budget is < 64 MB.

## Testing instructions

- .NET: xUnit, tests under `server-dotnet/tests/` and `client-dotnet/tests/`. Cover chunking, hashing, token bucket, manifest validation, protocol error codes.
- Python: pytest under `client-python/tests/`. Mirror the .NET test scenarios so both clients are known-equivalent.
- `scripts/e2e.py` must pass before merging: it boots the server, uploads a real file with the Python client, and asserts the SHA-256 of the file in the vault.
- All tests and the e2e run must pass before opening a PR.

## PR & commit conventions

- Branch from `main`; never push directly to `main`.
- Conventional commits: `feat:` / `fix:` / `docs:` / `refactor:` / `test:` / `chore:`.
- One logical change per PR. If the protocol changes, say so explicitly in the PR description and list every affected component.
- PR title in the imperative mood, body explaining *why* and the observable behaviour change.
- CI (`.github/workflows/ci.yml`) must be green before merge.

## Security

- Never commit secrets. `.env`, `*.db` and `appsettings.Local.json` are git-ignored.
- TLS 1.3 in production; access tokens are short-lived JWTs, refresh tokens are single-use and stored hashed.
- Chunk data must never be logged. Log metadata (ids, sizes, hashes) only.
- The receiver writes unverified data to `Staging/` and only to a vault after the global SHA-256 matches — keep that invariant intact.
- Report vulnerabilities privately per `SECURITY.md`; do not open a public issue for a security report.
