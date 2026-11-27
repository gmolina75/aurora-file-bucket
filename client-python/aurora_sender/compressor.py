"""Adaptive zstd compression (PROTOCOL §4.3, SPEC §2 and §8).

Two rules from the protocol drive this module:

* **adaptive cutoff** -- when the compressed size of a chunk exceeds 95% of the
  raw size, that chunk is transferred uncompressed (``X-Compression: none``);
* **compressibility probe** -- the first 2 MB of the file are compressed before
  the transfer starts and, if the reduction is below 8%, compression is
  disabled for the whole session.
"""

from __future__ import annotations

import zstandard

__all__ = [
    "COMPRESSION_NONE",
    "COMPRESSION_ZSTD",
    "CUTOFF_RATIO",
    "PROBE_BYTES",
    "PROBE_MIN_REDUCTION",
    "ZSTD_LEVEL",
    "compress_chunk",
    "compression_is_useful",
    "probe_reduction",
]

ZSTD_LEVEL = 3
CUTOFF_RATIO = 0.95
PROBE_BYTES = 2 * 1024 * 1024
PROBE_MIN_REDUCTION = 0.08

COMPRESSION_ZSTD = "zstd"
COMPRESSION_NONE = "none"


def _compressor(level: int = ZSTD_LEVEL) -> zstandard.ZstdCompressor:
    """Compressor used for both the probe and the per-chunk compression."""
    return zstandard.ZstdCompressor(level=level)


def compress_chunk(raw: bytes, level: int = ZSTD_LEVEL) -> tuple[bytes, str]:
    """Compress one chunk honouring the 95% cutoff.

    Returns the bytes to transfer plus the value for the ``X-Compression``
    header (``"zstd"`` or ``"none"``).
    """
    if not raw:
        return raw, COMPRESSION_NONE
    compressed = _compressor(level).compress(raw)
    if len(compressed) > CUTOFF_RATIO * len(raw):
        return raw, COMPRESSION_NONE
    return compressed, COMPRESSION_ZSTD


def probe_reduction(sample: bytes, level: int = ZSTD_LEVEL) -> float:
    """Fraction of bytes saved by compressing ``sample`` (0.0 .. 1.0)."""
    if not sample:
        return 0.0
    compressed = _compressor(level).compress(sample)
    return (len(sample) - len(compressed)) / len(sample)


def compression_is_useful(
    sample: bytes,
    min_reduction: float = PROBE_MIN_REDUCTION,
    level: int = ZSTD_LEVEL,
) -> bool:
    """Whether compression is worth keeping for the whole session."""
    return probe_reduction(sample, level) >= min_reduction
