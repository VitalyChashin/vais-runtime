"""Unit tests for vais_agent_sdk runner — mocks stdin/stdout, verifies framing and dispatch."""
from __future__ import annotations

import asyncio
import json
from typing import Optional
import pytest

from vais_agent_sdk._models import AgentRequest, AgentResponse
from vais_agent_sdk._runner import _dispatch, _result, _error


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _req(**kw) -> dict:
    base = {
        "jsonrpc": "2.0",
        "id": "1",
        "method": "vais/agent.invoke",
        "params": {
            "agentId": "my-agent",
            "sessionId": "sess-1",
            "userMessage": "hello",
            "state": None,
            "timeoutSeconds": 60,
            "context": None,
        },
    }
    base.update(kw)
    return base


def _collect_output(monkeypatch) -> list[str]:
    lines: list[str] = []

    def fake_send(line: str) -> None:
        lines.append(line)

    monkeypatch.setattr("vais_agent_sdk._runner._send", fake_send)
    return lines


# ---------------------------------------------------------------------------
# _result / _error helpers
# ---------------------------------------------------------------------------

def test_result_shape():
    out = json.loads(_result("42", {"foo": "bar"}))
    assert out == {"jsonrpc": "2.0", "id": "42", "result": {"foo": "bar"}}


def test_error_shape():
    out = json.loads(_error("99", -32600, "bad request"))
    assert out == {
        "jsonrpc": "2.0",
        "id": "99",
        "error": {"code": -32600, "message": "bad request"},
    }


# ---------------------------------------------------------------------------
# initialize
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_initialize_returns_server_info(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def noop(req): ...  # type: ignore[return]

    await _dispatch({"jsonrpc": "2.0", "id": "0", "method": "initialize", "params": {}}, noop, None)

    assert len(lines) == 1
    resp = json.loads(lines[0])
    assert resp["id"] == "0"
    assert "serverInfo" in resp["result"]
    assert resp["result"]["serverInfo"]["name"] == "vais-agent-sdk"


# ---------------------------------------------------------------------------
# tools/list
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_tools_list_returns_empty(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def noop(req): ...  # type: ignore[return]

    await _dispatch({"jsonrpc": "2.0", "id": "1", "method": "tools/list", "params": {}}, noop, None)

    resp = json.loads(lines[0])
    assert resp["result"] == {"tools": []}


# ---------------------------------------------------------------------------
# vais/agent.invoke — success
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_invoke_calls_user_function_and_returns_response(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:
        assert req.user_message == "hello"
        assert req.state is None
        return AgentResponse(assistantMessage="Hi!", newState='{"count":1}')

    await _dispatch(_req(), my_invoke, None)

    resp = json.loads(lines[0])
    assert resp["result"]["assistantMessage"] == "Hi!"
    assert resp["result"]["newState"] == '{"count":1}'


@pytest.mark.asyncio
async def test_invoke_passes_state_to_user_function(monkeypatch):
    lines = _collect_output(monkeypatch)
    received_state: list[Optional[str]] = []

    async def my_invoke(req: AgentRequest) -> AgentResponse:
        received_state.append(req.state)
        return AgentResponse(assistantMessage="ok")

    msg = _req()
    msg["params"]["state"] = '{"prior": true}'
    await _dispatch(msg, my_invoke, None)

    assert received_state[0] == '{"prior": true}'


@pytest.mark.asyncio
async def test_invoke_null_new_state_omitted_from_response(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:
        return AgentResponse(assistantMessage="ok")  # newState defaults to None

    await _dispatch(_req(), my_invoke, None)

    resp = json.loads(lines[0])
    assert "newState" not in resp["result"]


# ---------------------------------------------------------------------------
# vais/agent.invoke — error handling
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_invoke_user_exception_returns_json_rpc_error(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:
        raise ValueError("something went wrong")

    await _dispatch(_req(), my_invoke, None)

    resp = json.loads(lines[0])
    assert "error" in resp
    assert "python-agent-invoke-failed" in resp["error"]["message"]
    assert "ValueError" in resp["error"]["message"]


@pytest.mark.asyncio
async def test_invoke_error_preserves_request_id(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:
        raise RuntimeError("boom")

    msg = _req()
    msg["id"] = "req-42"
    await _dispatch(msg, my_invoke, None)

    resp = json.loads(lines[0])
    assert resp["id"] == "req-42"


# ---------------------------------------------------------------------------
# vais/agent.reset
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_reset_calls_on_reset_hook(monkeypatch):
    lines = _collect_output(monkeypatch)
    reset_calls: list[str] = []

    async def my_invoke(req: AgentRequest) -> AgentResponse:  # type: ignore[return]
        ...

    async def my_reset(session_id: str) -> None:
        reset_calls.append(session_id)

    msg = {
        "jsonrpc": "2.0",
        "id": "r1",
        "method": "vais/agent.reset",
        "params": {"agentId": "agent-1", "sessionId": "sess-99"},
    }
    await _dispatch(msg, my_invoke, my_reset)

    assert reset_calls == ["sess-99"]
    resp = json.loads(lines[0])
    assert resp["result"] == {}


@pytest.mark.asyncio
async def test_reset_without_hook_returns_empty_result(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:  # type: ignore[return]
        ...

    msg = {
        "jsonrpc": "2.0",
        "id": "r2",
        "method": "vais/agent.reset",
        "params": {"sessionId": "sess-1"},
    }
    await _dispatch(msg, my_invoke, None)

    resp = json.loads(lines[0])
    assert resp["result"] == {}


# ---------------------------------------------------------------------------
# Unknown method
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_unknown_method_returns_method_not_found(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:  # type: ignore[return]
        ...

    await _dispatch(
        {"jsonrpc": "2.0", "id": "x", "method": "unknown/method", "params": {}},
        my_invoke,
        None,
    )

    resp = json.loads(lines[0])
    assert resp["error"]["code"] == -32601


@pytest.mark.asyncio
async def test_notification_does_not_produce_response(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:  # type: ignore[return]
        ...

    await _dispatch(
        {"jsonrpc": "2.0", "method": "initialized"},  # no "id" → notification
        my_invoke,
        None,
    )

    assert lines == []


# ---------------------------------------------------------------------------
# ping
# ---------------------------------------------------------------------------

@pytest.mark.asyncio
async def test_ping_returns_empty_result(monkeypatch):
    lines = _collect_output(monkeypatch)

    async def my_invoke(req: AgentRequest) -> AgentResponse:  # type: ignore[return]
        ...

    await _dispatch({"jsonrpc": "2.0", "id": "p1", "method": "ping"}, my_invoke, None)

    resp = json.loads(lines[0])
    assert resp["result"] == {}
