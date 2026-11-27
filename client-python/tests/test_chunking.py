"""Chunk geometry (PROTOCOL §4.3)."""

from __future__ import annotations

import pytest

from aurora_sender.chunking import part_offset, part_raw_size, plan_parts, total_parts

MIB = 1024 * 1024

CASES = [
    # (total_bytes, chunk_size, expected total parts, expected last part size)
    (1, 4 * MIB, 1, 1),
    (4 * MIB, 4 * MIB, 1, 4 * MIB),
    (4 * MIB + 1, 4 * MIB, 2, 1),
    (9 * MIB, 4 * MIB, 3, 1 * MIB),
    (100 * MIB, 16 * MIB, 7, 4 * MIB),
    (107374182400, 16 * MIB, 6400, 16 * MIB),
    (10_000, 4 * MIB, 1, 10_000),
]


@pytest.mark.parametrize(
    "total_bytes, chunk_size, expected_parts, expected_last_size",
    CASES,
)
def test_total_parts(total_bytes: int, chunk_size: int, expected_parts: int, expected_last_size: int) -> None:
    assert total_parts(total_bytes, chunk_size) == expected_parts


@pytest.mark.parametrize(
    "total_bytes, chunk_size, expected_parts, expected_last_size",
    CASES,
)
def test_offsets_and_sizes(
    total_bytes: int, chunk_size: int, expected_parts: int, expected_last_size: int
) -> None:
    parts = plan_parts(total_bytes, chunk_size)
    assert len(parts) == expected_parts
    assert parts[0].offset == 0
    assert parts[-1].raw_size == expected_last_size

    # The parts tile the file exactly, without gaps or overlaps.
    assert sum(part.raw_size for part in parts) == total_bytes
    for index, part in enumerate(parts):
        assert part.number == index + 1
        assert part.offset == index * chunk_size
        assert part.raw_size <= chunk_size
    for previous, current in zip(parts, parts[1:]):
        assert previous.offset + previous.raw_size == current.offset


def test_last_part_is_the_short_one() -> None:
    total_bytes = 9 * MIB
    chunk_size = 4 * MIB
    sizes = [part.raw_size for part in plan_parts(total_bytes, chunk_size)]
    assert sizes == [4 * MIB, 4 * MIB, 1 * MIB]


def test_total_parts_is_ceiling_division() -> None:
    for total_bytes in range(1, 40):
        chunk_size = 7
        expected = -(-total_bytes // chunk_size)
        assert total_parts(total_bytes, chunk_size) == expected


def test_part_helpers_agree_with_plan() -> None:
    parts = plan_parts(9 * MIB, 4 * MIB)
    for part in parts:
        assert part_offset(part.number, 4 * MIB) == part.offset
        assert part_raw_size(part.number, 9 * MIB, 4 * MIB) == part.raw_size


def test_invalid_inputs_are_rejected() -> None:
    with pytest.raises(ValueError):
        total_parts(0, 4 * MIB)
    with pytest.raises(ValueError):
        total_parts(10, 0)
    with pytest.raises(ValueError):
        part_offset(0, 4 * MIB)
