"""Tests for session-mode call-token renewal (TokenManager + gateway 401 retry)."""
from __future__ import annotations

import functools
import time

import httpx

from vais_plugin import AsyncLlmClient, RequestContext
from vais_plugin._tokens import TokenManager


def _mock_async_client(handler):
    """Returns a partial that injects a MockTransport, mirroring the existing gateway tests."""
    return functools.partial(httpx.AsyncClient, transport=httpx.MockTransport(handler))


def _renew_response(token: str = "new-token", ttl: float = 120.0):
    return httpx.Response(200, json={"token": token, "expiresAt": time.time() + ttl})


# ── pass-through (short-turn plugins) ─────────────────────────────────────────

async def test_no_renew_url_is_passthrough():
    tm = TokenManager(RequestContext(call_token="tok"), "a")
    assert tm.can_renew is False
    headers = await tm.auth_headers()
    assert headers["Authorization"] == "Bearer tok"


# ── proactive refresh before expiry ───────────────────────────────────────────

async def test_proactive_refresh_when_near_expiry(monkeypatch):
    monkeypatch.setattr(httpx, "AsyncClient", _mock_async_client(lambda req: _renew_response()))
    tm = TokenManager(RequestContext(call_token="old-token", renew_url="http://gw/token/renew"), "a")
    tm._deadline = time.monotonic() - 1  # current token already past its deadline
    headers = await tm.auth_headers()
    assert headers["Authorization"] == "Bearer new-token"


async def test_no_refresh_while_token_is_fresh(monkeypatch):
    calls = {"n": 0}

    def handler(req):
        calls["n"] += 1
        return _renew_response()

    monkeypatch.setattr(httpx, "AsyncClient", _mock_async_client(handler))
    tm = TokenManager(RequestContext(call_token="old-token", renew_url="http://gw/token/renew"), "a")
    tm._deadline = time.monotonic() + 600  # comfortably fresh
    headers = await tm.auth_headers()
    assert headers["Authorization"] == "Bearer old-token"
    assert calls["n"] == 0


# ── reactive refresh on 401 (gateway round trip) ──────────────────────────────

async def test_llm_client_refreshes_and_retries_on_401(monkeypatch):
    def handler(req: httpx.Request) -> httpx.Response:
        if req.url.path.endswith("/renew"):
            return _renew_response()
        if req.headers.get("authorization") == "Bearer old-token":
            return httpx.Response(401, json={})
        return httpx.Response(200, json={"message": {"content": "ok"}})

    monkeypatch.setattr(httpx, "AsyncClient", _mock_async_client(handler))
    ctx = RequestContext(call_token="old-token", renew_url="http://gw/token/renew")
    resp = await AsyncLlmClient("http://gw/llm/", ctx, "a").complete([])
    assert resp.content == "ok"


# ── dedupe: a stale 401 after another coroutine already rotated is a no-op ─────

async def test_refresh_if_stale_skips_when_already_rotated(monkeypatch):
    calls = {"n": 0}

    def handler(req):
        calls["n"] += 1
        return _renew_response()

    monkeypatch.setattr(httpx, "AsyncClient", _mock_async_client(handler))
    tm = TokenManager(RequestContext(call_token="old-token", renew_url="http://gw/token/renew"), "a")
    await tm.refresh_if_stale("old-token")
    assert tm.current_token == "new-token"
    await tm.refresh_if_stale("old-token")  # stale; already rotated
    assert tm.current_token == "new-token"
    assert calls["n"] == 1
