"""Tests for the registry-driven vais_extension FastAPI Host seam routing.

These pin the Host's actual current behavior so the seam-routing refactor (one
``_SeamSpec`` row per seam instead of an isinstance chain) is guarded, and so adding
the tool/llm seams later cannot silently break agentInput/agentOutput dispatch.

Note: the outer envelope fields are snake_case here (``call_id`` / ``continuation_token``)
because that is what the Host parses today. A separate C#<->Python envelope-casing
mismatch is tracked outside this refactor; these tests deliberately do not assert the
envelope-casing contract, only the routing + context building.
"""
from __future__ import annotations

from fastapi.testclient import TestClient

from vais_extension import AgentInputMiddleware, AgentOutputMiddleware, Host
from vais_extension.wire import (
    AgentInputContext,
    AgentOutputContext,
    PostResponse,
    PreResponse,
)


class _RecordingInput(AgentInputMiddleware):
    def __init__(self) -> None:
        self.pre_ctx: AgentInputContext | None = None
        self.pre_call_id: str | None = None
        self.post_args: tuple[str, str | None] | None = None

    async def pre(self, context: AgentInputContext, call_id: str) -> PreResponse:
        self.pre_ctx = context
        self.pre_call_id = call_id
        return PreResponse(action="mutate", continuation_token="ct-1", context_patch={"k": "v"})

    async def post(self, call_id: str, continuation_token: str | None) -> PostResponse:
        self.post_args = (call_id, continuation_token)
        return PostResponse(action="mutate", context_patch={"after": 1})


class _ShortCircuitOutput(AgentOutputMiddleware):
    def __init__(self) -> None:
        self.pre_ctx: AgentOutputContext | None = None

    async def pre(self, context: AgentOutputContext, call_id: str) -> PreResponse:
        self.pre_ctx = context
        return PreResponse(action="shortCircuit")


def _client(handlers: dict) -> TestClient:
    return TestClient(Host("ext-test", "0.1.0", handlers, target_api_version="0.30").fastapi)


def test_discovery_advertises_each_handler_seam():
    client = _client({"in": _RecordingInput(), "out": _ShortCircuitOutput()})
    body = client.get("/v1/handlers").json()

    assert body["extensionId"] == "ext-test"
    assert body["targetApiVersion"] == "0.30"
    advertised = {h["id"]: h for h in body["handlers"]}
    assert advertised["in"]["seam"] == "agentInput"
    assert advertised["out"]["seam"] == "agentOutput"
    assert advertised["in"]["preEndpoint"] == "/handlers/in/pre"
    assert advertised["in"]["postEndpoint"] == "/handlers/in/post"


def test_input_pre_builds_context_and_echoes_response():
    inp = _RecordingInput()
    client = _client({"in": inp})

    resp = client.post("/handlers/in/pre", json={
        "call_id": "c1",
        "context": {"agentId": "a1", "runId": "r1", "nodeId": "n1", "message": "hello"},
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "mutate"
    assert data["continuation_token"] == "ct-1"
    assert data["context_patch"] == {"k": "v"}

    assert isinstance(inp.pre_ctx, AgentInputContext)
    assert (inp.pre_ctx.agent_id, inp.pre_ctx.run_id, inp.pre_ctx.node_id, inp.pre_ctx.message) == \
        ("a1", "r1", "n1", "hello")
    assert inp.pre_call_id == "c1"


def test_input_post_dispatches_to_handler():
    inp = _RecordingInput()
    client = _client({"in": inp})

    resp = client.post("/handlers/in/post", json={"call_id": "c1", "continuation_token": "ct-1"})

    assert resp.status_code == 200
    assert resp.json()["action"] == "mutate"
    assert inp.post_args == ("c1", "ct-1")


def test_output_pre_builds_output_context_and_short_circuits():
    out = _ShortCircuitOutput()
    client = _client({"out": out})

    resp = client.post("/handlers/out/pre", json={
        "call_id": "c2",
        "context": {"agentId": "a2", "runId": "r2", "sessionId": "s2",
                    "outputTokens": 5, "inputTokens": 3},
    })

    assert resp.status_code == 200
    assert resp.json()["action"] == "shortCircuit"
    assert isinstance(out.pre_ctx, AgentOutputContext)
    assert (out.pre_ctx.agent_id, out.pre_ctx.session_id,
            out.pre_ctx.output_tokens, out.pre_ctx.input_tokens) == ("a2", "s2", 5, 3)
