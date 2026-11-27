# Aurora File Bucket

Open-source monorepo for reliable, resumable, and integrity-verified transfer of massive files (up to **100 GB**) to a cloud server running **Windows Server + IIS + ASP.NET**.

## Repository Structure

- `server-dotnet/` — ASP.NET Core receiver hosted on IIS with streaming chunk ingestion, Zstandard decompression, SHA-256 verification, immediate staging cleanup, vault isolation, and SignalR real-time admin dashboard.
- `client-dotnet/` — Native .NET 8/9 CLI sender for **Windows** workstations and servers.
- `client-python/` — Python 3.12+ CLI sender for **Linux** servers and pipelines.
- `landing-page/` — Static landing page deployed automatically to **GitHub Pages** (`github.io`).
- `contracts/openapi.yaml` — Shared v1 HTTP contract for control plane, chunk upload, and rotatable token refresh.
- `docs/SPEC.md` — Full technical specification.
