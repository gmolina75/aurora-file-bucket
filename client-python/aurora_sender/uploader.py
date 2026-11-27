"""Transfer engine.

Responsibilities:

* resume an existing session or start a new one (PROTOCOL §4.2);
* read the file **once**, sequentially, computing the global SHA-256 while the
  chunks are read (PROTOCOL §4.3);
* compress every chunk with the adaptive cutoff and hand it to the workers;
* keep a global token bucket in front of the network writes;
* queue the assembly and wait for the server to report ``READY``.
"""

from __future__ import annotations

import queue
import threading
import time
from dataclasses import dataclass

from .api import ApiClient, ApiError
from .chunking import plan_parts
from .compressor import COMPRESSION_NONE, compress_chunk, compression_is_useful
from .config import SenderConfig
from .hashing import IncrementalSha256, sha256_hex
from .progress import ProgressReporter
from .state import SessionRecord, StateStore
from .tokenbucket import TokenBucket

__all__ = ["SessionPlan", "TransferResult", "Uploader"]

ASSEMBLY_TIMEOUT_SECONDS = 7200.0
ASSEMBLY_POLL_SECONDS = 2.0
QUEUE_DEPTH = 1
FINAL_STATES = ("READY", "FAILED", "EXPIRED")


@dataclass(frozen=True)
class SessionPlan:
    """The session the sender works on and the parts the server already has."""

    upload_id: str
    chunk_size: int
    total_parts: int
    total_bytes: int
    received_parts: frozenset[int] = frozenset()
    resumed: bool = False
    vault_slug: str = ""


@dataclass(frozen=True)
class TransferResult:
    """Outcome of a transfer."""

    upload_id: str
    file_sha256: str
    status: str
    parts_uploaded: int
    parts_skipped: int
    total_parts: int
    bytes_transferred: int
    duration_seconds: float
    vault_path: str | None = None
    final_sha256: str | None = None
    error_code: str | None = None


@dataclass
class _PartJob:
    """A chunk ready to be transferred."""

    number: int
    payload: bytes
    compression: str
    raw_size: int
    chunk_sha256: str
    raw_sha256: str


class Uploader:
    """Runs one file transfer."""

    def __init__(
        self,
        config: SenderConfig,
        api: ApiClient,
        state: StateStore,
        reporter: ProgressReporter,
        bucket: TokenBucket | None = None,
        sleeper=time.sleep,
    ) -> None:
        self._config = config
        self._api = api
        self._state = state
        self._reporter = reporter
        self._bucket = bucket or TokenBucket(config.bandwidth_bytes_per_second)
        self._sleep = sleeper
        self._hash = IncrementalSha256()
        self._queue: queue.Queue[_PartJob | None] = queue.Queue(maxsize=QUEUE_DEPTH)
        self._cancel = threading.Event()
        self._reader_done = threading.Event()
        self._error: BaseException | None = None
        self._error_lock = threading.Lock()
        self._session: SessionPlan | None = None
        self._parts_uploaded = 0
        self._parts_skipped = 0
        self._bytes_sent = 0

    # ------------------------------------------------------------------
    # public API
    # ------------------------------------------------------------------
    @property
    def file_sha256(self) -> str:
        """Global digest of the file, computed while reading it."""
        return self._hash.hexdigest()

    def run(self) -> TransferResult:
        """Upload the file and wait until the server reports the result."""
        started = time.monotonic()
        stat = self._config.file_path.stat()
        total_bytes = stat.st_size
        if total_bytes <= 0:
            raise ValueError(f"{self._config.file_path} is empty")

        compression_enabled = self._probe_compression()
        session = self.resolve_session(total_bytes, stat.st_mtime, compression_enabled)
        self._upload_parts(session, compression_enabled)

        file_sha256 = self._hash.hexdigest()
        self._api.complete(session.upload_id, file_sha256, notify=self._config.notify)
        status = self._wait_for_assembly(session.upload_id)

        final_state = str(status.get("status") or "UNKNOWN")
        self._state.update_state(session.upload_id, final_state, file_sha256)
        if final_state != "READY":
            error = status.get("error") or {}
            raise ApiError(
                0,
                str((error or {}).get("code") or "assembly_failed"),
                str((error or {}).get("message") or f"assembly ended in state {final_state}"),
            )

        return TransferResult(
            upload_id=session.upload_id,
            file_sha256=file_sha256,
            status=final_state,
            parts_uploaded=self._parts_uploaded,
            parts_skipped=self._parts_skipped,
            total_parts=session.total_parts,
            bytes_transferred=self._bytes_sent,
            duration_seconds=time.monotonic() - started,
            vault_path=status.get("vaultPath"),
            final_sha256=status.get("finalSha256"),
        )

    def resolve_session(
        self,
        total_bytes: int,
        file_mtime: float,
        compression_enabled: bool = True,
    ) -> SessionPlan:
        """Resume an existing session or start a new one (PROTOCOL §4.2).

        The server is the authority: when a session can be resumed its
        ``receivedParts`` replace whatever the local database remembers.
        """
        chunk_size = self._config.chunk_size
        if self._config.resume:
            record = self._state.find_session(
                str(self._config.file_path),
                total_bytes,
                self._api.base_url,
                self._config.vault_slug,
            )
            if record is not None and abs(record.file_mtime - file_mtime) < 1e-6:
                session = self._adopt_session(record, chunk_size)
                if session is not None:
                    return session

        return self._start_session(total_bytes, chunk_size, compression_enabled)

    # ------------------------------------------------------------------
    # session handling
    # ------------------------------------------------------------------
    def _adopt_session(self, record: SessionRecord, chunk_size: int) -> SessionPlan | None:
        """Ask the server what it really has and adopt that as the truth."""
        try:
            status = self._api.get_status(record.upload_id)
        except ApiError:
            return None

        server_state = str(status.get("status") or "")
        if server_state in FINAL_STATES:
            return None

        server_chunk = int(status.get("chunkSize") or 0)
        if server_chunk != chunk_size:
            # The manifest of a session is immutable: a different chunk size
            # needs a new session.
            return None

        total_parts = int(status.get("totalParts") or 0)
        received = frozenset(int(number) for number in (status.get("receivedParts") or []))
        session = SessionPlan(
            upload_id=record.upload_id,
            chunk_size=server_chunk,
            total_parts=total_parts,
            total_bytes=record.file_size,
            received_parts=received,
            resumed=True,
            vault_slug=str(status.get("vaultSlug") or record.vault_slug),
        )
        self._state.save_session(
            SessionRecord(
                file_path=record.file_path,
                file_size=record.file_size,
                file_mtime=record.file_mtime,
                server_url=record.server_url,
                vault_slug=record.vault_slug,
                upload_id=session.upload_id,
                chunk_size=session.chunk_size,
                total_parts=session.total_parts,
                file_sha256=record.file_sha256,
                state="UPLOADING",
                updated_at=_timestamp(),
            )
        )
        return session

    def _start_session(
        self,
        total_bytes: int,
        chunk_size: int,
        compression_enabled: bool,
    ) -> SessionPlan:
        """Create a new upload session (PROTOCOL §2.1)."""
        total_parts = -(-total_bytes // chunk_size)
        payload = {
            "fileName": self._config.file_path.name,
            "totalBytes": total_bytes,
            "chunkSize": chunk_size,
            "totalParts": total_parts,
            "fileSha256": None,
            "compression": "auto" if compression_enabled else "none",
        }
        response = self._api.init_upload(payload, self._config.vault_slug or None)
        upload_id = str(response["uploadId"])
        session = SessionPlan(
            upload_id=upload_id,
            chunk_size=int(response.get("chunkSize") or chunk_size),
            total_parts=int(response.get("totalParts") or total_parts),
            total_bytes=total_bytes,
            received_parts=frozenset(int(number) for number in (response.get("receivedParts") or [])),
            resumed=False,
            vault_slug=str(response.get("vaultSlug") or self._config.vault_slug),
        )
        self._state.save_session(
            SessionRecord(
                file_path=str(self._config.file_path),
                file_size=total_bytes,
                file_mtime=self._config.file_path.stat().st_mtime,
                server_url=self._api.base_url,
                vault_slug=self._config.vault_slug,
                upload_id=upload_id,
                chunk_size=session.chunk_size,
                total_parts=session.total_parts,
                file_sha256=None,
                state="UPLOADING",
                updated_at=_timestamp(),
            )
        )
        return session

    def _probe_compression(self) -> bool:
        """Decide whether compression is worth it for the whole session."""
        if not self._config.compress:
            return False
        with self._config.file_path.open("rb") as handle:
            sample = handle.read(2 * 1024 * 1024)
        return compression_is_useful(sample)

    # ------------------------------------------------------------------
    # transfer
    # ------------------------------------------------------------------
    def _upload_parts(self, session: SessionPlan, compression_enabled: bool) -> None:
        self._session = session
        self._cancel.clear()
        self._reader_done.clear()
        self._queue = queue.Queue(maxsize=QUEUE_DEPTH)

        workers = [
            threading.Thread(target=self._worker, name=f"aurora-part-{index}", daemon=True)
            for index in range(self._config.concurrency)
        ]
        reader = threading.Thread(
            target=self._reader, args=(compression_enabled,), name="aurora-reader", daemon=True
        )
        for worker in workers:
            worker.start()
        reader.start()

        reader.join()
        for worker in workers:
            worker.join()

        if self._error is not None:
            raise self._error

    def _reader(self, compression_enabled: bool) -> None:
        """Read the file sequentially, hashing it and queueing the chunks."""
        session = self._session
        assert session is not None
        try:
            with self._config.file_path.open("rb") as handle:
                for spec in plan_parts(session.total_bytes, session.chunk_size):
                    if self._cancel.is_set():
                        return
                    handle.seek(spec.offset)
                    raw = handle.read(spec.raw_size)
                    if len(raw) != spec.raw_size:
                        raise OSError(f"short read at offset {spec.offset}")
                    # The global digest is fed here: the file is read once.
                    self._hash.update(raw)
                    if spec.number in session.received_parts:
                        self._parts_skipped += 1
                        self._reporter.on_part_skipped(spec.number, spec.raw_size)
                        continue
                    if compression_enabled:
                        payload, method = compress_chunk(raw)
                    else:
                        payload, method = raw, COMPRESSION_NONE
                    job = _PartJob(
                        number=spec.number,
                        payload=payload,
                        compression=method,
                        raw_size=spec.raw_size,
                        chunk_sha256=sha256_hex(payload),
                        raw_sha256=sha256_hex(raw),
                    )
                    del raw
                    self._enqueue(job)
        except BaseException as error:  # noqa: BLE001 - reported to the caller
            self._fail(error)
        finally:
            self._reader_done.set()
            self._enqueue_sentinel()

    def _worker(self) -> None:
        """Transfer the queued chunks, one part at a time."""
        session = self._session
        assert session is not None
        while True:
            if self._cancel.is_set():
                return
            try:
                job = self._queue.get(timeout=0.25)
            except queue.Empty:
                if self._reader_done.is_set():
                    return
                continue
            if job is None:
                return
            try:
                # The token bucket throttles the bytes written to the network.
                self._bucket.acquire(len(job.payload))
                self._api.put_part(
                    session.upload_id,
                    job.number,
                    job.payload,
                    chunk_sha256=job.chunk_sha256,
                    raw_sha256=job.raw_sha256,
                    raw_size=job.raw_size,
                    compression=job.compression,
                )
                self._state.record_part(
                    session.upload_id,
                    job.number,
                    job.compression,
                    len(job.payload),
                    job.raw_size,
                    job.chunk_sha256,
                )
                self._reporter.on_part_uploaded(job.number, len(job.payload))
                self._parts_uploaded += 1
                self._bytes_sent += len(job.payload)
            except BaseException as error:  # noqa: BLE001 - reported to the caller
                self._fail(error)
                return

    def _enqueue(self, job: _PartJob) -> None:
        while not self._cancel.is_set():
            try:
                self._queue.put(job, timeout=0.25)
                return
            except queue.Full:
                continue

    def _enqueue_sentinel(self) -> None:
        while not self._cancel.is_set():
            try:
                self._queue.put(None, timeout=0.25)
                return
            except queue.Full:
                continue

    def _fail(self, error: BaseException) -> None:
        with self._error_lock:
            if self._error is None:
                self._error = error
        self._cancel.set()

    def _wait_for_assembly(self, upload_id: str) -> dict:
        """Poll ``GET /status`` until the session reaches a final state."""
        deadline = time.monotonic() + ASSEMBLY_TIMEOUT_SECONDS
        while True:
            status = self._api.get_status(upload_id)
            state = str(status.get("status") or "")
            if state in FINAL_STATES:
                return status
            if time.monotonic() >= deadline:
                raise TimeoutError(
                    f"assembly of {upload_id} did not finish within {ASSEMBLY_TIMEOUT_SECONDS:.0f}s"
                )
            self._sleep(ASSEMBLY_POLL_SECONDS)

    # ------------------------------------------------------------------
    # counters
    # ------------------------------------------------------------------
    @property
    def parts_uploaded(self) -> int:
        """Parts transferred in this run."""
        return self._parts_uploaded

    @property
    def parts_skipped(self) -> int:
        """Parts the server already had and that were not transferred again."""
        return self._parts_skipped

    @property
    def bytes_transferred(self) -> int:
        """Bytes written to the network in this run."""
        return self._bytes_sent


def _timestamp() -> str:
    from datetime import datetime, timezone

    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
