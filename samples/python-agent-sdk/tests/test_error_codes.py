"""Typed error encoding + gateway auto-emission in vais_agent_sdk (stdio parity with the HTTP SDK)."""
from __future__ import annotations

import asyncio
import functools
import json

import httpx
import pytest

from vais_agent_sdk import LlmGatewayError, ToolError
from vais_agent_sdk._runner import _dispatch, _typed_error
from vais_agent_sdk.langchain_compat import gateway_get_tools
from vais_agent_sdk.sections import RequestSections, complete_from_sections


def _req(**params) -> dict:
    base = {
        "agentId": "my-agent",
        "sessionId": "sess-1",
        "userMessage": "hi",
        "state": None,
        "timeoutSeconds": 60,
        "context": None,
    }
    base.update(params)
    return {"jsonrpc": "2.0", "id": "1", "method": "vais/agent.invoke", "params": base}


def _collect(monkeypatch) -> list[str]:
    lines: list[str] = []
    monkeypatch.setattr("vais_agent_sdk._runner._send", lambda line: lines.append(line))
    return lines


def test_typed_error_encoding():
    out = json.loads(_typed_error("7", "Timeout", "took too long"))
    assert out["error"]["code"] == -32000
    assert out["error"]["data"]["errorType"] == "Timeout"
    assert "[vais.errorType=Timeout]" in out["error"]["message"]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "exc,error_type",
    [
        (LlmGatewayError("boom"), "LlmGatewayError"),
        (ToolError("boom"), "ToolError"),
        (ValueError("boom"), "InternalError"),
    ],
)
async def test_invoke_maps_error_type(monkeypatch, exc, error_type):
    lines = _collect(monkeypatch)

    async def my_invoke(req):
        raise exc

    await _dispatch(_req(), my_invoke, None, None)
    resp = json.loads(lines[0])
    assert resp["error"]["data"]["errorType"] == error_type


@pytest.mark.asyncio
async def test_invoke_timeout_maps_to_timeout(monkeypatch):
    lines = _collect(monkeypatch)

    async def slow(req):
        await asyncio.sleep(5)  # exceeds the 1s budget below

    await _dispatch(_req(timeoutSeconds=1), slow, None, None)
    resp = json.loads(lines[0])
    assert resp["error"]["data"]["errorType"] == "Timeout"


@pytest.mark.asyncio
async def test_complete_from_sections_raises_llm_gateway_error_on_5xx():
    client = httpx.AsyncClient(
        transport=httpx.MockTransport(lambda req: httpx.Response(502, json={"e": "x"}))
    )
    try:
        with pytest.raises(LlmGatewayError):
            await complete_from_sections(
                gateway_base_url="http://gw",
                call_token="t",
                run_id="r",
                agent_id="a",
                sections=RequestSections(),
                client=client,
            )
    finally:
        await client.aclose()


@pytest.mark.asyncio
async def test_gateway_get_tools_raises_tool_error_on_non_2xx(monkeypatch):
    monkeypatch.setattr(
        "vais_agent_sdk.langchain_compat.httpx.AsyncClient",
        functools.partial(
            httpx.AsyncClient,
            transport=httpx.MockTransport(lambda req: httpx.Response(503, json={"e": "x"})),
        ),
    )
    with pytest.raises(ToolError):
        await gateway_get_tools("http://gw", "t", "r", "a")
