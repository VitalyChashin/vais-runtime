"""Session-mode call-token manager: proactive + reactive (401) renewal.

Short-turn plugins receive a single token and no renew URL, so the manager is a pass-through.
Session-mode plugins (``spec.sessionTtlSeconds`` set) receive a short token plus a renew URL;
the manager refreshes the token before it expires and, as a safety net, after a 401 from the
gateway. One manager is shared across the LLM and tool clients for a single invoke so a renewal
by one surface is immediately seen by the other.
"""

from __future__ import annotations

import asyncio
import time

import httpx

from .models import LlmGatewayError, RequestContext


class TokenManager:
    """Owns the current call token for one invoke and renews it on demand."""

    def __init__(self, context: RequestContext, agent_id: str, *, skew_seconds: float = 15.0) -> None:
        self._token = context.call_token
        self._renew_url = context.renew_url
        self._run_id = context.run_id
        self._traceparent = context.traceparent
        self._agent_id = agent_id
        self._skew = skew_seconds
        # Monotonic deadline of the current token; None until the first renewal tells us the expiry
        # (the initial token's lifetime is not advertised on the wire, so we lean on the 401 path).
        self._deadline: float | None = None
        self._lock = asyncio.Lock()

    @property
    def can_renew(self) -> bool:
        return bool(self._renew_url)

    @property
    def current_token(self) -> str:
        return self._token

    async def auth_headers(self) -> dict[str, str]:
        """Returns request headers, proactively renewing first if the token is near expiry."""
        if self.can_renew and self._near_expiry():
            async with self._lock:
                if self._near_expiry():
                    await self._renew()
        return self._headers(self._token)

    async def refresh_if_stale(self, stale_token: str) -> None:
        """Force a renewal after a 401 — unless another coroutine already rotated the token."""
        if not self.can_renew:
            return
        async with self._lock:
            if self._token != stale_token:
                return
            await self._renew()

    def _headers(self, token: str) -> dict[str, str]:
        h = {"Authorization": f"Bearer {token}", "X-Agent-Id": self._agent_id}
        if self._run_id:
            h["X-Run-Id"] = self._run_id
        if self._traceparent:
            h["traceparent"] = self._traceparent
        return h

    def _near_expiry(self) -> bool:
        return self._deadline is not None and time.monotonic() >= self._deadline - self._skew

    async def _renew(self) -> None:
        """POSTs the current token to the renew URL and rotates to the fresh one. Caller holds the lock."""
        assert self._renew_url is not None
        headers = {"Authorization": f"Bearer {self._token}", "X-Agent-Id": self._agent_id}
        if self._run_id:
            headers["X-Run-Id"] = self._run_id
        async with httpx.AsyncClient() as client:
            resp = await client.post(self._renew_url, headers=headers)
        if resp.status_code >= 400:
            raise LlmGatewayError(
                f"Call-token renewal failed: HTTP {resp.status_code}: {resp.text[:200]}"
            )
        data = resp.json()
        self._token = data["token"]
        expires_at = data.get("expiresAt")
        if expires_at is not None:
            # expiresAt is absolute Unix seconds; convert to a local monotonic deadline.
            self._deadline = time.monotonic() + max(0.0, float(expires_at) - time.time())
