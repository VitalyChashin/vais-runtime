"""Integration test for the SectionedAgent end-to-end flow (SC-24).

Drives one ``invoke()`` call against an in-memory httpx.MockTransport that stands in
for the runtime gateway. Verifies that the plugin:

1. Calls /v1/sections/build with the right URL + headers + plugin's messages view.
2. Round-trips the returned Section[] back to /v1/container-gateway/llm/complete via
   the canonical v0.27 ``complete_from_sections`` path — so the runtime runs flatten +
   telemetry server-side, restoring per-section observability symmetry with runtime-hosted
   agents (the regression the v0.27 contract bump fixed).
3. Returns the assistant content + usage in an AgentResponse.

No live runtime required — the test is the smoke check the SC-24 acceptance asks for,
plus a deterministic regression guard against future plugin / SDK drift.
"""
from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from vais_agent_sdk import AgentRequest

from sectioned_plugin.agent import invoke


def _build_request() -> AgentRequest:
    return AgentRequest.model_validate({
        "agentId": "research-helper",
        "sessionId": "sess-abc",
        "userMessage": "What's our return policy?",
        "context": {
            "llmGatewayUrl": "http://gateway.local",
            "callToken": "tok-xyz",
            "runId": "run-42",
        },
    })


def _sections_response() -> dict[str, Any]:
    """Three sections matching the SC-21 happy-path test: persona, retrieval, history."""
    return {
        "sections": [
            {
                "id": "system.persona",
                "kind": "SystemSegment",
                "payload": {"value": "You are a careful research assistant."},
                "order": 0,
                "producerId": "PersonaContributor",
            },
            {
                "id": "retrieval.docs",
                "kind": "SystemSegment",
                "payload": {"value": "Source 1: Returns within 30 days."},
                "producerId": "KnowledgeRetrievalContextProvider",
                "budget": {"priority": 5},
            },
            {
                "id": "history.window.0",
                "kind": "UserMessage",
                "payload": {"turn": {"role": "user", "content": "What's our return policy?"}},
                "producerId": "Base",
            },
        ],
        "totalChars": 110,
    }


def _llm_complete_response() -> dict[str, Any]:
    return {
        "message": {
            "role": "assistant",
            "content": "Returns are accepted within 30 days. Source 1.",
        },
        "usage": {"inputTokens": 42, "outputTokens": 13, "cachedTokens": 0},
    }


async def test_invoke_end_to_end_routes_through_canonical_v0_27_path():
    captured: list[dict[str, Any]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        captured.append({
            "method": request.method,
            "url": str(request.url),
            "headers": dict(request.headers),
            "body": json.loads(request.content),
        })
        if request.url.path == "/v1/container-gateway/sections/build":
            return httpx.Response(200, json=_sections_response())
        if request.url.path == "/v1/container-gateway/llm/complete":
            return httpx.Response(200, json=_llm_complete_response())
        return httpx.Response(404, text=f"unhandled: {request.url}")

    client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    try:
        resp = await invoke(_build_request(), client=client)
    finally:
        await client.aclose()

    # Two calls, in order: sections/build then llm/complete (canonical v0.27 path).
    assert len(captured) == 2
    assert captured[0]["url"].endswith("/v1/container-gateway/sections/build")
    assert captured[1]["url"].endswith("/v1/container-gateway/llm/complete")

    # Both calls carry the bearer + correlation headers.
    for call in captured:
        assert call["headers"]["authorization"] == "Bearer tok-xyz"
        assert call["headers"]["x-run-id"] == "run-42"
        assert call["headers"]["x-agent-id"] == "research-helper"

    # sections/build receives the plugin's messages view.
    assert captured[0]["body"] == {
        "messages": [{"role": "user", "content": "What's our return policy?"}],
    }

    # llm/complete receives the SECTIONS variant — not the flattened messages array. This is
    # the v0.27 fix: the plugin ships sections back so the runtime runs flatten + telemetry
    # server-side, instead of the plugin reimplementing flatten and losing per-section
    # observability on the LLM-call span.
    llm_body = captured[1]["body"]
    assert "sections" in llm_body
    assert "messages" not in llm_body  # mutually exclusive per the discriminator rule
    assert llm_body["modelId"] == "gpt-4o-mini"
    assert len(llm_body["sections"]) == 3
    section_ids = [s["id"] for s in llm_body["sections"]]
    assert section_ids == ["system.persona", "retrieval.docs", "history.window.0"]

    # Response carries the assistant reply + usage round-trip.
    assert resp.assistant_message == "Returns are accepted within 30 days. Source 1."
    assert resp.usage is not None and len(resp.usage) == 1
    assert resp.usage[0].input_tokens == 42
    assert resp.usage[0].output_tokens == 13
    assert resp.usage[0].model == "gpt-4o-mini"


async def test_invoke_propagates_404_from_unknown_agent():
    """If /v1/sections/build returns 404 (unknown agent), the helper raises and the
    plugin author sees an httpx.HTTPStatusError to react to (fall back to the default
    InvokeRequest.messages path, log, alert, etc.)."""
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/v1/container-gateway/sections/build":
            return httpx.Response(404, json={"title": "Agent not found."})
        return httpx.Response(500, text="should not be reached")

    client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    try:
        with pytest.raises(httpx.HTTPStatusError) as exc:
            await invoke(_build_request(), client=client)
        assert exc.value.response.status_code == 404
    finally:
        await client.aclose()
