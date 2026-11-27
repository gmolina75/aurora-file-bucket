"""Defaults, validation and the CLI merge for the Aurora sender.

The flag names, defaults and ranges are the ones fixed by PROTOCOL §4.3 and
shared with the .NET client, so both senders are interchangeable.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass
from pathlib import Path

__all__ = [
    "CLIENT_PLATFORM",
    "DEFAULT_CHUNK_SIZE_MIB",
    "DEFAULT_CONCURRENCY",
    "DEFAULT_MAX_BANDWIDTH_MBPS",
    "DEFAULT_MAX_RETRIES",
    "DEFAULT_STATE_DB",
    "MAX_CHUNK_SIZE_MIB",
    "MAX_CONCURRENCY",
    "MAX_MAX_BANDWIDTH_MBPS",
    "MIN_CHUNK_SIZE_MIB",
    "MIN_CONCURRENCY",
    "MIN_MAX_BANDWIDTH_MBPS",
    "MIB",
    "SenderConfig",
    "TOKEN_REFRESH_THRESHOLD_SECONDS",
    "build_config",
]

MIB = 1024 * 1024

DEFAULT_CHUNK_SIZE_MIB = 16
MIN_CHUNK_SIZE_MIB = 4
MAX_CHUNK_SIZE_MIB = 32

DEFAULT_CONCURRENCY = 3
MIN_CONCURRENCY = 1
MAX_CONCURRENCY = 6

DEFAULT_MAX_BANDWIDTH_MBPS = 50
MIN_MAX_BANDWIDTH_MBPS = 1
MAX_MAX_BANDWIDTH_MBPS = 1000

DEFAULT_MAX_RETRIES = 5
DEFAULT_STATE_DB = "aurora_state.db"

CLIENT_PLATFORM = "linux-python"
TOKEN_REFRESH_THRESHOLD_SECONDS = 180


@dataclass(frozen=True)
class SenderConfig:
    """Everything the sender needs to run one transfer."""

    server_url: str
    file_path: Path
    vault_slug: str
    email: str | None
    password: str | None
    access_token: str | None
    chunk_size_mib: int
    concurrency: int
    max_bandwidth_mbps: int
    compress: bool
    state_db: Path
    max_retries: int
    resume: bool
    json_progress: bool
    notify: bool
    client_id: str

    @property
    def chunk_size(self) -> int:
        """Chunk size in bytes."""
        return self.chunk_size_mib * MIB

    @property
    def bandwidth_bytes_per_second(self) -> float:
        """Global bandwidth limit in bytes per second."""
        return self.max_bandwidth_mbps * 1_000_000 / 8


def build_config(args: object) -> SenderConfig:
    """Merge the parsed CLI arguments into a :class:`SenderConfig`."""
    server_url = str(getattr(args, "server")).strip().rstrip("/")
    if not server_url:
        raise ValueError("--server must not be empty")

    file_path = Path(str(getattr(args, "file"))).expanduser()
    if not file_path.is_absolute():
        file_path = Path.cwd() / file_path
    file_path = Path(file_path.resolve())

    email = getattr(args, "email")
    password = getattr(args, "password")
    token = getattr(args, "token")
    if not token and not (email and password):
        raise ValueError("either --token or both --email and --password are required")

    return SenderConfig(
        server_url=server_url,
        file_path=file_path,
        vault_slug=(getattr(args, "vault") or ""),
        email=email,
        password=password,
        access_token=token,
        chunk_size_mib=int(getattr(args, "chunk_size_mib")),
        concurrency=int(getattr(args, "concurrency")),
        max_bandwidth_mbps=int(getattr(args, "max_bandwidth_mbps")),
        compress=not bool(getattr(args, "no_compress")),
        state_db=Path(str(getattr(args, "state_db"))).expanduser(),
        max_retries=int(getattr(args, "max_retries")),
        resume=not bool(getattr(args, "no_resume")),
        json_progress=bool(getattr(args, "json_progress")),
        notify=True,
        client_id=str(uuid.uuid4()),
    )
