#!/usr/bin/env python3
"""End-to-end proof for Aurora File Bucket.

Boots the real ASP.NET Core receiver on a throwaway port and a throwaway data
directory, uploads a real file with the real Python client, and asserts that
the file landed in the vault with a bit-for-bit identical SHA-256 and that the
staging area was reclaimed.

This is the only test in the repo that proves the two halves of the protocol
actually agree.

Usage:
    python scripts/e2e.py                 # full run
    python scripts/e2e.py --keep          # keep the scratch dir for inspection
    python scripts/e2e.py --size-mib 64   # bigger payload
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SERVER_PROJECT = REPO / "server-dotnet" / "AuroraFileBucket.Server.csproj"
CLIENT_PACKAGE = REPO / "client-python"

E2E_EMAIL = "e2e@example.com"
E2E_PASSWORD = "e2e-secret"
JWT_KEY = "e2e-local-signing-key-that-is-long-enough-0123456789"


class StepFailure(RuntimeError):
    """A step of the end-to-end scenario failed."""


def log(message: str) -> None:
    print(f"[e2e] {message}", flush=True)


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def sha256_file(path: Path, chunk_size: int = 1 << 20) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(chunk_size), b""):
            digest.update(block)
    return digest.hexdigest()


def http_json(
    url: str,
    payload: dict | None = None,
    method: str = "POST",
    timeout: float = 30.0,
) -> tuple[int, dict]:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(
        url, data=data, method=method, headers={"Content-Type": "application/json"}
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
            return response.status, json.loads(body) if body else {}
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8")
        try:
            return error.code, json.loads(body) if body else {}
        except json.JSONDecodeError:
            return error.code, {"raw": body}


def build_server(work: Path) -> Path:
    output = work / "server"
    log(f"building server -> {output}")
    result = subprocess.run(
        [
            "dotnet",
            "build",
            str(SERVER_PROJECT),
            "-c",
            "Release",
            "-o",
            str(output),
            "--nologo",
            "-v",
            "quiet",
        ],
        cwd=str(REPO),
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise StepFailure(
            "server build failed:\n"
            + result.stdout[-4000:]
            + "\n"
            + result.stderr[-4000:]
        )
    dll = output / "AuroraFileBucket.Server.dll"
    if not dll.exists():
        raise StepFailure(f"expected server dll at {dll}")
    return output


def start_server(server_dir: Path, work: Path, port: int) -> subprocess.Popen:
    staging = work / "staging"
    vaults = work / "vaults"
    staging.mkdir(parents=True, exist_ok=True)
    vaults.mkdir(parents=True, exist_ok=True)

    env = dict(os.environ)
    env.update(
        {
            "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
            "AuroraStorage__StagingRoot": str(staging),
            "AuroraStorage__VaultsRoot": str(vaults),
            "AuroraStorage__DatabasePath": str(work / "aurora.db"),
            "Auth__JwtSigningKey": JWT_KEY,
            "Auth__AllowedAdminEmails__0": E2E_EMAIL,
            "Auth__Users__0__Email": E2E_EMAIL,
            "Auth__Users__0__Password": E2E_PASSWORD,
            "Auth__Users__0__DisplayName": "E2E Runner",
            "Auth__Users__0__Vaults__0": "default",
        }
    )

    log(f"starting server on 127.0.0.1:{port}")
    process = subprocess.Popen(
        ["dotnet", str(server_dir / "AuroraFileBucket.Server.dll")],
        cwd=str(server_dir),
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )

    deadline = time.time() + 120
    while time.time() < deadline:
        if process.poll() is not None:
            output = process.stdout.read() if process.stdout else ""
            raise StepFailure(f"server exited early with code {process.returncode}:\n{output[-4000:]}")
        try:
            status, _ = http_json(
                f"http://127.0.0.1:{port}/v1/auth/login",
                {"email": E2E_EMAIL, "password": E2E_PASSWORD},
                timeout=5,
            )
            if status == 200:
                log("server is up and the dev user authenticates")
                return process
        except (urllib.error.URLError, OSError, TimeoutError):
            pass
        time.sleep(1)

    raise StepFailure("server did not become ready within 120s")


def make_test_file(path: Path, size_mib: int) -> None:
    """Half compressible, half random, so both compression paths get exercised."""
    log(f"creating {size_mib} MiB test file")
    with path.open("wb") as handle:
        compressible = min(size_mib // 2, 16)
        for _ in range(compressible):
            handle.write(b"\0" * (1 << 20))
        remaining = size_mib - compressible
        while remaining > 0:
            block = min(remaining, 16)
            handle.write(os.urandom((1 << 20) * block))
            remaining -= block


def resolve_client_command() -> list[str]:
    """Find a working way to invoke the Python client."""
    candidates: list[list[str]] = [
        [sys.executable, "-m", "aurora_sender.cli"],
        ["aurora-send"],
    ]
    for candidate in candidates:
        try:
            probe = subprocess.run(
                candidate + ["--help"],
                cwd=str(CLIENT_PACKAGE),
                capture_output=True,
                text=True,
                timeout=60,
            )
        except (OSError, subprocess.SubprocessError):
            continue
        if probe.returncode == 0 and "--server" in (probe.stdout + probe.stderr):
            log(f"client entry point: {' '.join(candidate)}")
            return candidate
    raise StepFailure(
        "could not find a working Python client entry point "
        "(tried 'python -m aurora_sender.cli' and 'aurora-send'). "
        "Install it with: python -m pip install -e ./client-python"
    )


def run_client(base_url: str, source: Path, work: Path) -> subprocess.CompletedProcess:
    command = resolve_client_command() + [
        "--server",
        base_url,
        "--file",
        str(source),
        "--email",
        E2E_EMAIL,
        "--password",
        E2E_PASSWORD,
        "--vault",
        "default",
        "--state-db",
        str(work / "client-state.db"),
        "--chunk-size-mib",
        "4",
        "--concurrency",
        "2",
        "--max-bandwidth-mbps",
        "500",
    ]
    log("running client: " + " ".join(command))
    return subprocess.run(command, cwd=str(CLIENT_PACKAGE), capture_output=True, text=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size-mib", type=int, default=48, help="payload size in MiB")
    parser.add_argument("--keep", action="store_true", help="keep the scratch directory")
    parser.add_argument(
        "--client-cmd",
        default=None,
        help="override the client command (first token is the interpreter)",
    )
    args = parser.parse_args()

    work = Path(tempfile.mkdtemp(prefix="aurora-e2e-"))
    server_process: subprocess.Popen | None = None
    try:
        log(f"scratch directory: {work}")
        server_dir = build_server(work)
        port = free_port()
        base_url = f"http://127.0.0.1:{port}"
        server_process = start_server(server_dir, work, port)

        source = work / "payload.bin"
        make_test_file(source, args.size_mib)
        expected = sha256_file(source)
        log(f"source sha256: {expected}")

        if args.client_cmd:
            command = args.client_cmd.split()
            command += [
                "--server",
                base_url,
                "--file",
                str(source),
                "--email",
                E2E_EMAIL,
                "--password",
                E2E_PASSWORD,
                "--vault",
                "default",
                "--state-db",
                str(work / "client-state.db"),
            ]
            result = subprocess.run(command, cwd=str(CLIENT_PACKAGE), capture_output=True, text=True)
        else:
            result = run_client(base_url, source, work)

        if result.stdout:
            print(result.stdout.rstrip())
        if result.returncode != 0:
            print(result.stderr.rstrip(), file=sys.stderr)
            raise StepFailure(f"client exited with code {result.returncode}")

        vault_dir = work / "vaults" / "default"
        candidates = [p for p in vault_dir.glob("*") if p.is_file()] if vault_dir.exists() else []
        candidates = [p for p in candidates if not p.name.endswith(".assembling")]
        if len(candidates) != 1:
            raise StepFailure(
                f"expected exactly 1 file in the vault, found {len(candidates)}: {candidates}"
            )
        landed = candidates[0]
        landed_hash = sha256_file(landed)
        log(f"vault file: {landed.name} ({landed.stat().st_size} bytes)")
        if landed_hash != expected:
            raise StepFailure(f"hash mismatch: vault {landed_hash} != source {expected}")

        staging = work / "staging"
        leftovers = [p for p in staging.glob("*") if p.is_dir()] if staging.exists() else []
        if leftovers:
            raise StepFailure(f"staging was not reclaimed: {leftovers}")

        log("OK: file transferred, SHA-256 matches bit for bit, staging reclaimed")
        return 0

    except StepFailure as error:
        print(f"[e2e] FAILED: {error}", file=sys.stderr)
        return 1
    except Exception as error:  # noqa: BLE001 - report anything unexpected
        print(f"[e2e] ERROR: {error!r}", file=sys.stderr)
        return 1
    finally:
        if server_process is not None and server_process.poll() is None:
            server_process.terminate()
            try:
                server_process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                server_process.kill()
        if args.keep:
            log(f"scratch directory kept at {work}")
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
