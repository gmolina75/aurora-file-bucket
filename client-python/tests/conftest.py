"""Shared pytest fixtures for the Aurora sender tests."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

PACKAGE_ROOT = Path(__file__).resolve().parent.parent
if str(PACKAGE_ROOT) not in sys.path:
    sys.path.insert(0, str(PACKAGE_ROOT))


@pytest.fixture()
def source_file(tmp_path: Path) -> Path:
    """A small, mostly compressible file used by the transfer tests."""
    path = tmp_path / "payload.bin"
    block = b"\x00" * (1 << 20)
    with path.open("wb") as handle:
        for _ in range(9):
            handle.write(block)
    return path
