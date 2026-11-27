"""Global token bucket shared by every worker thread (PROTOCOL §4.3).

The bucket holds one second worth of traffic (its capacity) and refills at
``Mbps * 1_000_000 / 8`` bytes per second.  Because a single chunk can be
larger than the bucket, the balance is allowed to go negative: the debt is
paid back with the tokens accrued afterwards, which keeps the *average* rate
exactly at the configured limit while still allowing the initial burst.

A single instance is shared by all upload workers, so the limit is global and
not per thread.
"""

from __future__ import annotations

import threading
import time
from typing import Callable

__all__ = ["TokenBucket"]


class TokenBucket:
    """Thread safe rate limiter used to throttle the bytes written to the wire."""

    def __init__(
        self,
        rate_bytes_per_second: float,
        capacity: float | None = None,
        clock: Callable[[], float] = time.monotonic,
        sleeper: Callable[[float], None] = time.sleep,
    ) -> None:
        if rate_bytes_per_second <= 0:
            raise ValueError("rate must be positive")
        self._rate = float(rate_bytes_per_second)
        self._capacity = float(capacity if capacity is not None else rate_bytes_per_second)
        self._clock = clock
        self._sleep = sleeper
        self._balance = self._capacity
        self._last = clock()
        self._lock = threading.Lock()

    @property
    def rate(self) -> float:
        """Refill rate in bytes per second."""
        return self._rate

    @property
    def capacity(self) -> float:
        """Maximum burst, in bytes (one second of traffic by default)."""
        return self._capacity

    def acquire(self, amount: int) -> None:
        """Block until ``amount`` bytes may be written to the network."""
        if amount <= 0:
            return
        while True:
            with self._lock:
                self._refill_locked()
                if self._balance >= amount:
                    self._balance -= amount
                    return
                wait = (amount - self._balance) / self._rate
            # Sleep outside the lock so the other workers keep making progress.
            self._sleep(wait)

    def _refill_locked(self) -> None:
        now = self._clock()
        elapsed = now - self._last
        if elapsed > 0:
            # `min` keeps the burst capped while a negative balance (debt) is
            # paid back token by token.
            self._balance = min(self._capacity, self._balance + elapsed * self._rate)
            self._last = now
