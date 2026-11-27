"""Progress reporting for the transfer.

Human readable by default (``[ 12.3%] 787/6400 parts | 48.1 MB/s | ETA
00:14:22``); with ``--json-progress`` every event is a single JSON object per
line on stdout, ready for a machine consumer.
"""

from __future__ import annotations

import json
import sys
import threading
import time
from collections import deque
from typing import TextIO

__all__ = ["ProgressReporter"]

WINDOW_SECONDS = 5.0
BYTES_PER_MIB = 1024 * 1024


class ProgressReporter:
    """Thread safe progress reporter."""

    def __init__(
        self,
        total_parts: int,
        total_bytes: int,
        json_mode: bool = False,
        stream: TextIO | None = None,
        clock=time.monotonic,
        window_seconds: float = WINDOW_SECONDS,
    ) -> None:
        self._total_parts = total_parts
        self._total_bytes = total_bytes
        self._json = json_mode
        self._stream = sys.stdout if stream is None else stream
        self._clock = clock
        self._window = window_seconds
        self._lock = threading.Lock()
        self._parts_done = 0
        self._parts_skipped = 0
        self._bytes_sent = 0
        self._bytes_total = 0
        self._started = clock()
        self._samples: deque[tuple[float, int]] = deque()
        self._last_percent = -1

    # ------------------------------------------------------------------
    # events
    # ------------------------------------------------------------------
    def on_part_uploaded(self, part_number: int, transferred_bytes: int) -> None:
        """A part was transferred."""
        with self._lock:
            self._parts_done += 1
            self._bytes_sent += transferred_bytes
            self._bytes_total += transferred_bytes
            self._samples.append((self._clock(), self._bytes_sent))
            self._trim_samples()
            payload = self._snapshot(part_number)

        if self._json:
            self._write_json(payload)
        else:
            self._write_human(payload)

    def on_part_skipped(self, part_number: int, raw_bytes: int) -> None:
        """A part confirmed by the server was skipped (resume)."""
        with self._lock:
            self._parts_skipped += 1
            self._bytes_total += raw_bytes
            payload = self._snapshot(part_number)

        if self._json:
            self._write_json({**payload, "event": "skipped"})

    def summary(self, extra: dict[str, object] | None = None) -> None:
        """Final line of the transfer."""
        with self._lock:
            payload = self._snapshot(None)
        payload["event"] = "summary"
        if extra:
            payload.update(extra)
        if self._json:
            self._write_json(payload)
        else:
            self._write_human(payload, final=True)

    # ------------------------------------------------------------------
    # internals
    # ------------------------------------------------------------------
    def _trim_samples(self) -> None:
        horizon = self._clock() - self._window
        while len(self._samples) > 2 and self._samples[1][0] < horizon:
            self._samples.popleft()

    def _rate_bytes_per_second(self) -> float:
        """Instantaneous rate over the sliding window."""
        if len(self._samples) < 2:
            return 0.0
        (first_time, first_bytes), (last_time, last_bytes) = self._samples[0], self._samples[-1]
        elapsed = last_time - first_time
        if elapsed <= 0:
            return 0.0
        return (last_bytes - first_bytes) / elapsed

    def _snapshot(self, part_number: int | None) -> dict[str, object]:
        rate = self._rate_bytes_per_second()
        done = self._parts_done + self._parts_skipped
        percent = (done / self._total_parts * 100) if self._total_parts else 0.0
        remaining_bytes = max(0, self._total_bytes - self._bytes_total)
        eta = (remaining_bytes / rate) if rate > 0 else None
        return {
            "event": "progress",
            "part": part_number,
            "partsDone": self._parts_done,
            "partsSkipped": self._parts_skipped,
            "partsTotal": self._total_parts,
            "percent": round(percent, 2),
            "bytesTransferred": self._bytes_sent,
            "bytesTotal": self._bytes_total,
            "mbps": round(rate / BYTES_PER_MIB, 2),
            "etaSeconds": None if eta is None else round(eta, 1),
        }

    def _write_json(self, payload: dict[str, object]) -> None:
        with self._lock:
            self._stream.write(json.dumps(payload, separators=(",", ":")) + "\n")
            self._stream.flush()

    def _write_human(self, payload: dict[str, object], final: bool = False) -> None:
        eta = payload["etaSeconds"]
        eta_text = "--:--:--" if eta is None else _format_duration(float(eta))
        line = (
            f"[{float(payload['percent']):6.2f}%] "
            f"{int(payload['partsDone']) + int(payload['partsSkipped'])}/{payload['partsTotal']} parts | "
            f"{float(payload['mbps']):.1f} MB/s | ETA {eta_text}"
        )
        interactive = getattr(self._stream, "isatty", lambda: False)()
        with self._lock:
            if interactive:
                self._stream.write(line + ("\n" if final else "\r"))
                self._stream.flush()
                return
            percent = int(float(payload["percent"]))
            if final or percent != self._last_percent:
                self._last_percent = percent
                self._stream.write(line + "\n")
                self._stream.flush()


def _format_duration(seconds: float) -> str:
    """Format a duration as ``HH:MM:SS``."""
    total = int(max(0, seconds))
    hours, remainder = divmod(total, 3600)
    minutes, secs = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}"
