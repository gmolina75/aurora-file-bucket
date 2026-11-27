"""Chunk geometry for the Aurora upload protocol (PROTOCOL §4.3).

The file is split into uniform chunks: every part holds ``chunk_size`` bytes
except the last one, which holds the remainder.  ``totalParts`` is exactly
``ceil(totalBytes / chunkSize)`` and part numbers are 1-based.
"""

from __future__ import annotations

from dataclasses import dataclass

__all__ = [
    "PartSpec",
    "part_offset",
    "part_raw_size",
    "plan_parts",
    "total_parts",
]


@dataclass(frozen=True)
class PartSpec:
    """Geometry of a single part inside the source file."""

    number: int
    offset: int
    raw_size: int


def total_parts(total_bytes: int, chunk_size: int) -> int:
    """Number of parts needed for a file of ``total_bytes`` bytes."""
    if total_bytes <= 0:
        raise ValueError("total_bytes must be positive")
    if chunk_size <= 0:
        raise ValueError("chunk_size must be positive")
    return -(-total_bytes // chunk_size)


def part_offset(part_number: int, chunk_size: int) -> int:
    """Byte offset of ``part_number`` (1-based) inside the source file."""
    if part_number < 1:
        raise ValueError("part numbers are 1-based")
    if chunk_size <= 0:
        raise ValueError("chunk_size must be positive")
    return (part_number - 1) * chunk_size


def part_raw_size(part_number: int, total_bytes: int, chunk_size: int) -> int:
    """Uncompressed size of ``part_number``; the last part is the short one."""
    offset = part_offset(part_number, chunk_size)
    return max(0, min(chunk_size, total_bytes - offset))


def plan_parts(total_bytes: int, chunk_size: int) -> list[PartSpec]:
    """Every part of the file, in order."""
    count = total_parts(total_bytes, chunk_size)
    return [
        PartSpec(
            number=number,
            offset=part_offset(number, chunk_size),
            raw_size=part_raw_size(number, total_bytes, chunk_size),
        )
        for number in range(1, count + 1)
    ]
