"""Tests for plugin error-type mapping (502/503/504) and gateway auto-emission."""
from __future__ import annotations

import asyncio
import functools
import json

import httpx
import pytest
from fastapi.testclient import TestClient

import vais_plugin.gateway as gw
from vais_plugin import (
    AsyncLlmClient,
    AsyncToolClient,
    InvokeResponse,
    LlmGatewayError,
    PluginAgent,
    RequestContext,
    ToolCall,
    ToolError,
    vais_plugin,
)


@vais_plugin("0.24")
class _Raiser(PluginAgent):
    """Maps the first user message to a controlled outcome, so each error path is exercisable."""

    async def invoke(self, request):
        kind = request.messages[0].content
        if kind == "ok":
            return InvokeResponse(assistant_message="hi")
        if kind == "llm":
            raise LlmGatewayError("gateway boom")
        if kind == "tool":
            raise ToolError("tool boom")
        if kind == "timeout":
            await asyncio.sleep(5)  # exceeds the 1s budget set by the test
        raise RuntimeError("internal boom")


def _client() -> TestClient:
    return TestClient(_Raiser()._build_app())


def _body(kind: str, timeout: int = 30) -> dict:
    return {
        "agentId": "a",
        "sessionId": "s",
        "messages": [{"role": "user", "content": kind}],
        "llmGatewayUrl": "http://x",
        "toolGatewayUrl": "http://y",
        "opaqueState": None,
        "timeoutSeconds": timeout,
        "context": {},
    }


def _sse_error_type(text: str) -> str | None:
    lines = text.splitlines()
    for i, line in enumerate(lines):
        if line.strip() == "event: error":
            for data_line in lines[i + 1:]:
                if data_line.startswith("data:"):
                    return json.loads(data_line[len("data:"):].strip())["errorType"]
    return None


# ── invoke status mapping (EC-5) ──────────────────────────────────────────────

@pytest.mark.parametrize(
    "kind,status,error_type",
    [
        ("ok", 200, None),
        ("llm", 502, "LlmGatewayError"),
        ("tool", 503, "ToolError"),
        ("internal", 500, "InternalError"),
    ],
)
def test_invoke_status_mapping(kind, status, error_type):
    resp = _client().post("/v1/invoke", json=_body(kind))
    assert resp.status_code == status
    if error_type is not None:
        assert resp.json()["errorType"] == error_type


def test_invoke_timeout_maps_to_504():
    resp = _client().post("/v1/invoke", json=_body("timeout", timeout=1))
    assert resp.status_code == 504
    assert resp.json()["errorType"] == "Timeout"


# ── stream error events (EC-5) ────────────────────────────────────────────────

def test_stream_emits_error_event_for_llm():
    resp = _client().post("/v1/stream", json=_body("llm"))
    assert resp.status_code == 200
    assert _sse_error_type(resp.text) == "LlmGatewayError"
    assert "event: done" in resp.text


def test_stream_timeout_emits_timeout_error_event():
    resp = _client().post("/v1/stream", json=_body("timeout", timeout=1))
    assert _sse_error_type(resp.text) == "Timeout"
    assert "event: done" in resp.text


# ── gateway client auto-emission (EC-6 + D1) ──────────────────────────────────

def _mock_client(status: int, body: dict):
    return functools.partial(
        httpx.AsyncClient,
        transport=httpx.MockTransport(lambda req: httpx.Response(status, json=body)),
    )


async def test_llm_client_raises_on_upstream_5xx(monkeypatch):
    monkeypatch.setattr(gw.httpx, "AsyncClient", _mock_client(502, {"error": "upstream"}))
    with pytest.raises(LlmGatewayError):
        await AsyncLlmClient("http://gw/", RequestContext(), "a").complete([])


async def test_tool_client_raises_on_dispatch_failure(monkeypatch):
    monkeypatch.setattr(gw.httpx, "AsyncClient", _mock_client(503, {"error": "no such tool"}))
    with pytest.raises(ToolError):
        await AsyncToolClient("http://gw/", RequestContext(), "a").invoke(ToolCall("1", "t", {}))


async def test_tool_client_returns_error_result_on_2xx(monkeypatch):
    # D1: a tool that ran and returned an error *result* (HTTP 200) is returned to the
    # plugin, not raised — preserving the catch-and-continue loop.
    body = {"toolCallId": "1", "content": "tool failed internally", "isError": True}
    monkeypatch.setattr(gw.httpx, "AsyncClient", _mock_client(200, body))
    result = await AsyncToolClient("http://gw/", RequestContext(), "a").invoke(ToolCall("1", "t", {}))
    assert result.is_error is True
    assert result.content == "tool failed internally"
