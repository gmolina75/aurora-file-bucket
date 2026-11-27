"""HTTP layer for the Aurora receiver (PROTOCOL §1, §2 and §4.4).

Owns authentication, the retry policy and the five protocol endpoints.  Every
request goes through :meth:`ApiClient._send`, which implements the retry rules
of PROTOCOL §4.4 literally:

===================  ==================================================
Situation            Behaviour
===================  ==================================================
429 + ``Retry-After``  wait exactly that many seconds, then retry
5xx / network / timeout  exponential backoff with jitter (1s, 2s, 4s, 8s, 16s)
401                    refresh the token once, then retry
400 ``chunk_hash_mismatch``  abort, never retry
409                    abort, never retry
===================  ==================================================
"""

from __future__ import annotations

import random
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Mapping
from urllib.parse import urlsplit, urlunsplit

import httpx

__all__ = [
    "ApiError",
    "ApiClient",
    "TokenPair",
    "TokenStore",
    "normalize_base_url",
]

CLIENT_PLATFORM = "linux-python"

RETRY_BACKOFF_SECONDS = (1, 2, 4, 8, 16)
JITTER_RATIO = 0.25
DEFAULT_RETRY_AFTER_SECONDS = 1.0
TOKEN_REFRESH_THRESHOLD_SECONDS = 180.0


class ApiError(RuntimeError):
    """Protocol level failure reported by the receiver (or the transport)."""

    def __init__(
        self,
        status_code: int,
        code: str,
        message: str,
        details: Mapping[str, Any] | None = None,
    ) -> None:
        super().__init__(f"HTTP {status_code} {code}: {message}")
        self.status_code = status_code
        self.code = code
        self.message = message
        self.details: dict[str, Any] = dict(details) if details else {}


def normalize_base_url(server: str) -> str:
    """Normalize ``--server`` into the API base URL.

    The protocol base URL is ``https://<host>/v1``; a value that already ends
    with ``/v1`` is kept as is.
    """
    text = server.strip().rstrip("/")
    if not text:
        raise ValueError("server URL must not be empty")
    if not text.lower().startswith(("http://", "https://")):
        text = "https://" + text
    parsed = urlsplit(text)
    path = parsed.path.rstrip("/")
    if not path.endswith("/v1"):
        path = f"{path}/v1"
    return urlunsplit((parsed.scheme, parsed.netloc, path, "", ""))


def parse_error(response: httpx.Response) -> tuple[str, str, dict[str, Any]]:
    """Extract ``(code, message, details)`` from an error response body."""
    code = f"http_{response.status_code}"
    message = response.text[:500]
    details: dict[str, Any] = {}
    try:
        payload = response.json()
    except ValueError:
        payload = None
    if isinstance(payload, dict):
        error = payload.get("error")
        if isinstance(error, dict):
            code = str(error.get("code") or code)
            message = str(error.get("message") or message)
            raw_details = error.get("details")
            if isinstance(raw_details, dict):
                details = dict(raw_details)
    return code, message, details


@dataclass
class TokenPair:
    """An access token plus its (single use) refresh token."""

    access_token: str
    refresh_token: str | None = None
    expires_in: int = 0
    issued_at: float = field(default_factory=time.time)

    @classmethod
    def from_payload(cls, payload: Mapping[str, Any]) -> "TokenPair":
        """Build a pair from a ``/auth/login`` or ``/auth/refresh` response."""
        return cls(
            access_token=str(payload["accessToken"]),
            refresh_token=payload.get("refreshToken"),
            expires_in=int(payload.get("expiresIn") or 0),
            issued_at=time.time(),
        )


class TokenStore:
    """Current token pair and the renewal rule of PROTOCOL §1.4.

    The token is renewed when less than three minutes of its TTL remain.
    """

    def __init__(
        self,
        access_token: str,
        refresh_token: str | None = None,
        expires_in: int = 0,
        issued_at: float | None = None,
        threshold_seconds: float = TOKEN_REFRESH_THRESHOLD_SECONDS,
    ) -> None:
        self._pair = TokenPair(
            access_token=access_token,
            refresh_token=refresh_token,
            expires_in=expires_in,
            issued_at=time.time() if issued_at is None else issued_at,
        )
        self._threshold = threshold_seconds
        self._lock = threading.Lock()

    @property
    def pair(self) -> TokenPair:
        """Current token pair."""
        with self._lock:
            return self._pair

    @property
    def access_token(self) -> str:
        """Current access token."""
        with self._lock:
            return self._pair.access_token

    @property
    def refresh_token(self) -> str | None:
        """Current refresh token, if any."""
        with self._lock:
            return self._pair.refresh_token

    def seconds_remaining(self, now: float | None = None) -> float:
        """Seconds of TTL left on the access token."""
        pair = self.pair
        reference = time.time() if now is None else now
        return pair.expires_in - (reference - pair.issued_at)

    def needs_refresh(self, now: float | None = None) -> bool:
        """Whether the access token must be renewed before the next request."""
        if self.pair.refresh_token is None:
            return False
        return self.seconds_remaining(now) < self._threshold

    def update(self, pair: TokenPair) -> None:
        """Replace the stored token pair."""
        with self._lock:
            self._pair = pair


class ApiClient:
    """Thin, policy aware client over the Aurora protocol endpoints."""

    def __init__(
        self,
        base_url: str,
        tokens: TokenStore,
        client_id: str,
        max_retries: int = 5,
        timeout: float = 60.0,
        transport: httpx.BaseTransport | None = None,
        sleeper: Callable[[float], None] = time.sleep,
        jitter: Callable[[], float] = random.random,
    ) -> None:
        self._base = normalize_base_url(base_url)
        self._tokens = tokens
        self._client_id = client_id
        self._max_retries = max(0, int(max_retries))
        self._sleep = sleeper
        self._jitter = jitter
        self._http = httpx.Client(timeout=timeout, transport=transport)

    @property
    def base_url(self) -> str:
        """Normalized API base URL, e.g. ``https://host/v1``."""
        return self._base

    @property
    def tokens(self) -> TokenStore:
        """Token store backing this client."""
        return self._tokens

    def close(self) -> None:
        """Close the underlying HTTP connection pool."""
        self._http.close()

    # ------------------------------------------------------------------
    # endpoints
    # ------------------------------------------------------------------
    def login(self, email: str, password: str) -> TokenPair:
        """``POST /v1/auth/login``."""
        response = self._send(
            "POST",
            "/auth/login",
            json_body={"email": email, "password": password},
            authenticated=False,
        )
        return TokenPair.from_payload(response.json())

    def refresh(self) -> TokenPair:
        """``POST /v1/auth/refresh``; rotates the stored token pair."""
        refresh_token = self._tokens.refresh_token
        if not refresh_token:
            raise ApiError(0, "no_refresh_token", "no refresh token available")
        response = self._send(
            "POST",
            "/auth/refresh",
            json_body={"refreshToken": refresh_token},
            authenticated=False,
            allow_refresh=False,
        )
        pair = TokenPair.from_payload(response.json())
        self._tokens.update(pair)
        return pair

    def init_upload(self, payload: Mapping[str, Any], vault_slug: str | None = None) -> dict[str, Any]:
        """``POST /v1/uploads/init``; returns the created session."""
        headers: dict[str, str] = {}
        if vault_slug:
            headers["X-Vault-Slug"] = vault_slug
        response = self._send("POST", "/uploads/init", json_body=dict(payload), headers=headers)
        return dict(response.json())

    def put_part(
        self,
        upload_id: str,
        part_number: int,
        payload: bytes,
        chunk_sha256: str,
        raw_sha256: str,
        raw_size: int,
        compression: str,
    ) -> dict[str, Any]:
        """``PUT /v1/uploads/{id}/parts/{n}``; returns the part receipt."""
        headers = {
            "Content-Type": "application/octet-stream",
            "X-Chunk-Sha256": chunk_sha256,
            "X-Compression": compression,
            "X-Raw-Size": str(raw_size),
            "X-Raw-Sha256": raw_sha256,
        }
        response = self._send(
            "PUT",
            f"/uploads/{upload_id}/parts/{part_number}",
            content=payload,
            headers=headers,
        )
        return dict(response.json())

    def get_status(self, upload_id: str) -> dict[str, Any]:
        """``GET /v1/uploads/{id}/status``."""
        response = self._send("GET", f"/uploads/{upload_id}/status")
        return dict(response.json())

    def complete(
        self,
        upload_id: str,
        file_sha256: str | None,
        notify: bool = True,
    ) -> dict[str, Any]:
        """``POST /v1/uploads/{id}/complete``; queues the assembly."""
        body: dict[str, Any] = {"fileSha256": file_sha256, "notify": notify}
        response = self._send("POST", f"/uploads/{upload_id}/complete", json_body=body)
        return dict(response.json())

    # ------------------------------------------------------------------
    # policy
    # ------------------------------------------------------------------
    def _common_headers(self) -> dict[str, str]:
        return {
            "X-Client-Id": self._client_id,
            "X-Client-Platform": CLIENT_PLATFORM,
        }

    def _backoff(self, attempt: int) -> float:
        """Exponential backoff with jitter: 1s, 2s, 4s, 8s, 16s."""
        base = RETRY_BACKOFF_SECONDS[min(attempt, len(RETRY_BACKOFF_SECONDS) - 1)]
        return base + base * JITTER_RATIO * self._jitter()

    @staticmethod
    def _retry_after(response: httpx.Response) -> float:
        """Seconds to wait, taken verbatim from the ``Retry-After`` header."""
        raw = response.headers.get("Retry-After")
        if raw is None:
            return DEFAULT_RETRY_AFTER_SECONDS
        try:
            return float(raw)
        except ValueError:
            return DEFAULT_RETRY_AFTER_SECONDS

    def _send(
        self,
        method: str,
        path: str,
        json_body: Mapping[str, Any] | None = None,
        content: bytes | None = None,
        headers: Mapping[str, str] | None = None,
        authenticated: bool = True,
        allow_refresh: bool = True,
    ) -> httpx.Response:
        """Send a request, applying the retry policy of PROTOCOL §4.4."""
        url = f"{self._base}{path}"
        attempt = 0
        refreshed = False
        while True:
            request_headers = self._common_headers()
            if authenticated:
                if self._tokens.needs_refresh():
                    self._renew()
                request_headers["Authorization"] = f"Bearer {self._tokens.access_token}"
            if headers:
                request_headers.update(headers)

            try:
                response = self._http.request(
                    method,
                    url,
                    json=json_body,
                    content=content,
                    headers=request_headers,
                )
            except (httpx.TimeoutException, httpx.TransportError) as error:
                if attempt >= self._max_retries:
                    raise ApiError(0, "network_error", str(error)) from error
                self._sleep(self._backoff(attempt))
                attempt += 1
                continue

            status = response.status_code

            if status == 429:
                if attempt >= self._max_retries:
                    code, message, details = parse_error(response)
                    raise ApiError(status, code, message, details)
                # The server dictates the delay: wait exactly Retry-After.
                self._sleep(self._retry_after(response))
                attempt += 1
                continue

            if status == 401:
                if allow_refresh and not refreshed and self._tokens.refresh_token:
                    refreshed = True
                    self._renew()
                    continue
                code, message, details = parse_error(response)
                raise ApiError(status, code, message, details)

            if status >= 500:
                if attempt >= self._max_retries:
                    code, message, details = parse_error(response)
                    raise ApiError(status, code, message, details)
                self._sleep(self._backoff(attempt))
                attempt += 1
                continue

            if status >= 400:
                code, message, details = parse_error(response)
                raise ApiError(status, code, message, details)

            return response

    def _renew(self) -> None:
        """Rotate the token pair (PROTOCOL §1.3)."""
        self.refresh()
