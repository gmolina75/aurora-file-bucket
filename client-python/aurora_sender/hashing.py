"""SHA-256 helpers.

Three digests are needed (PROTOCOL §2.2 and §4.3):

* ``X-Chunk-Sha256`` -- SHA-256 of the transferred body (the compressed bytes
  when zstd is used);
* ``X-Raw-Sha256`` -- SHA-256 of the uncompressed chunk;
* the global file digest -- SHA-256 of the original file, computed
  *incrementally* while the chunks are read so the file is never read twice.
"""

from __future__ import annotations

import hashlib
from typing import BinaryIO

__all__ = ["IncrementalSha256", "hash_stream", "sha256_hex"]


def sha256_hex(data: bytes) -> str:
    """Lowercase hex SHA-256 of ``data``."""
    return hashlib.sha256(data).hexdigest()


class IncrementalSha256:
    """SHA-256 accumulator fed with the raw bytes in file order."""

    __slots__ = ("_digest",)

    def __init__(self) -> None:
        self._digest = hashlib.sha256()

    def update(self, data: bytes) -> None:
        """Feed more bytes into the digest."""
        self._digest.update(data)

    def hexdigest(self) -> str:
        """Current digest as lowercase hex."""
        return self._digest.hexdigest()

    def copy(self) -> "IncrementalSha256":
        """Independent copy of the accumulator."""
        clone = IncrementalSha256()
        clone._digest = self._digest.copy()
        return clone


def hash_stream(handle: BinaryIO, block_size: int = 1 << 20) -> str:
    """SHA-256 of a whole stream, read in ``block_size`` blocks."""
    digest = hashlib.sha256()
    while True:
        block = handle.read(block_size)
        if not block:
            break
        digest.update(block)
    return digest.hexdigest()
