"""Local resume state (PROTOCOL §4.1).

The schema is the one fixed by the protocol and shared with the .NET client:
``upload_progress`` holds one row per (file, server, vault) and ``part_state``
holds one row per confirmed part.  The server stays the authority on which
parts exist (PROTOCOL §4.2); the local rows are only a hint.
"""

from __future__ import annotations

import sqlite3
import threading
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

__all__ = ["PartRecord", "SCHEMA", "SessionRecord", "StateStore"]

SCHEMA = """
CREATE TABLE IF NOT EXISTS upload_progress (
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

CREATE TABLE IF NOT EXISTS part_state (
  upload_id      TEXT NOT NULL,
  part_number    INTEGER NOT NULL,
  compression    TEXT,
  compressed_size INTEGER,
  raw_size       INTEGER,
  sha256         TEXT,
  uploaded_at    TEXT NOT NULL,
  PRIMARY KEY (upload_id, part_number)
);
"""


def _now() -> str:
    """RFC 3339 UTC timestamp, as used by the protocol."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


@dataclass(frozen=True)
class SessionRecord:
    """One row of ``upload_progress``."""

    file_path: str
    file_size: int
    file_mtime: float
    server_url: str
    vault_slug: str
    upload_id: str
    chunk_size: int
    total_parts: int
    file_sha256: str | None
    state: str
    updated_at: str


@dataclass(frozen=True)
class PartRecord:
    """One row of ``part_state``."""

    upload_id: str
    part_number: int
    compression: str | None
    compressed_size: int | None
    raw_size: int | None
    sha256: str | None
    uploaded_at: str


class StateStore:
    """SQLite backed resume state.

    A single connection guarded by a lock is used, so the store can be shared
    by the upload workers without the ``sqlite3`` thread restrictions.
    """

    def __init__(self, path: Path | str) -> None:
        self._path = Path(path)
        if self._path.parent and not self._path.parent.exists():
            self._path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.RLock()
        self._connection = sqlite3.connect(str(self._path), check_same_thread=False)
        self._connection.row_factory = sqlite3.Row
        with self._lock:
            self._connection.executescript(SCHEMA)
            self._connection.commit()

    @property
    def path(self) -> Path:
        """Path of the SQLite database."""
        return self._path

    def close(self) -> None:
        """Close the underlying connection."""
        with self._lock:
            self._connection.close()

    def find_session(
        self,
        file_path: str,
        file_size: int,
        server_url: str,
        vault_slug: str,
    ) -> SessionRecord | None:
        """Row matching ``(file_path, file_size, server_url, vault_slug)``."""
        with self._lock:
            row = self._connection.execute(
                "SELECT * FROM upload_progress"
                " WHERE file_path = ? AND file_size = ? AND server_url = ? AND vault_slug = ?",
                (file_path, file_size, server_url, vault_slug),
            ).fetchone()
        if row is None:
            return None
        return SessionRecord(
            file_path=row["file_path"],
            file_size=row["file_size"],
            file_mtime=row["file_mtime"],
            server_url=row["server_url"],
            vault_slug=row["vault_slug"],
            upload_id=row["upload_id"],
            chunk_size=row["chunk_size"],
            total_parts=row["total_parts"],
            file_sha256=row["file_sha256"],
            state=row["state"],
            updated_at=row["updated_at"],
        )

    def save_session(self, record: SessionRecord) -> None:
        """Insert or update the progress row of a session."""
        with self._lock:
            self._connection.execute(
                "INSERT INTO upload_progress ("
                " file_path, file_size, file_mtime, server_url, vault_slug, upload_id,"
                " chunk_size, total_parts, file_sha256, state, updated_at)"
                " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
                " ON CONFLICT (file_path, file_size, server_url, vault_slug) DO UPDATE SET"
                " file_mtime = excluded.file_mtime,"
                " upload_id = excluded.upload_id,"
                " chunk_size = excluded.chunk_size,"
                " total_parts = excluded.total_parts,"
                " file_sha256 = excluded.file_sha256,"
                " state = excluded.state,"
                " updated_at = excluded.updated_at",
                (
                    record.file_path,
                    record.file_size,
                    record.file_mtime,
                    record.server_url,
                    record.vault_slug,
                    record.upload_id,
                    record.chunk_size,
                    record.total_parts,
                    record.file_sha256,
                    record.state,
                    record.updated_at,
                ),
            )
            self._connection.commit()

    def update_state(self, upload_id: str, state: str, file_sha256: str | None = None) -> None:
        """Move a session to another state (``UPLOADING``, ``READY`` ...)."""
        with self._lock:
            self._connection.execute(
                "UPDATE upload_progress SET state = ?,"
                " file_sha256 = COALESCE(?, file_sha256),"
                " updated_at = ? WHERE upload_id = ?",
                (state, file_sha256, _now(), upload_id),
            )
            self._connection.commit()

    def delete_session(
        self,
        file_path: str,
        file_size: int,
        server_url: str,
        vault_slug: str,
    ) -> None:
        """Forget a session and its parts (``--no-resume``)."""
        with self._lock:
            self._connection.execute(
                "DELETE FROM part_state WHERE upload_id IN ("
                " SELECT upload_id FROM upload_progress"
                " WHERE file_path = ? AND file_size = ? AND server_url = ? AND vault_slug = ?)",
                (file_path, file_size, server_url, vault_slug),
            )
            self._connection.execute(
                "DELETE FROM upload_progress"
                " WHERE file_path = ? AND file_size = ? AND server_url = ? AND vault_slug = ?",
                (file_path, file_size, server_url, vault_slug),
            )
            self._connection.commit()

    def record_part(
        self,
        upload_id: str,
        part_number: int,
        compression: str,
        compressed_size: int,
        raw_size: int,
        sha256: str,
    ) -> None:
        """Remember a confirmed part."""
        with self._lock:
            self._connection.execute(
                "INSERT INTO part_state ("
                " upload_id, part_number, compression, compressed_size, raw_size, sha256, uploaded_at)"
                " VALUES (?, ?, ?, ?, ?, ?, ?)"
                " ON CONFLICT (upload_id, part_number) DO UPDATE SET"
                " compression = excluded.compression,"
                " compressed_size = excluded.compressed_size,"
                " raw_size = excluded.raw_size,"
                " sha256 = excluded.sha256,"
                " uploaded_at = excluded.uploaded_at",
                (upload_id, part_number, compression, compressed_size, raw_size, sha256, _now()),
            )
            self._connection.commit()

    def parts(self, upload_id: str) -> list[PartRecord]:
        """Parts recorded locally for a session (a hint, never the truth)."""
        with self._lock:
            rows = self._connection.execute(
                "SELECT * FROM part_state WHERE upload_id = ? ORDER BY part_number",
                (upload_id,),
            ).fetchall()
        return [
            PartRecord(
                upload_id=row["upload_id"],
                part_number=row["part_number"],
                compression=row["compression"],
                compressed_size=row["compressed_size"],
                raw_size=row["raw_size"],
                sha256=row["sha256"],
                uploaded_at=row["uploaded_at"],
            )
            for row in rows
        ]
