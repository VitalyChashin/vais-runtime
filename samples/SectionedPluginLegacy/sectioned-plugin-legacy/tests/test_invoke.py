"""Integration test for SectionedAgentLegacy — the plugin-side-flatten path.

Mirrors the canonical sample's test (``samples/SectionedPlugin/.../test_invoke.py``) so the
two are easy to diff. The shapes asserted here are intentionally *different* from the
canonical one — that's the point of the legacy sample.

Key assertions on this path:
- Two outbound calls: ``/v1/sections/build`` then ``/v1/container-gateway/chat/completions``
  (NOT ``/llm/complete`` like the canonical path).
- The LLM-call body carries a flat OpenAI-shape ``messages`` array (NOT ``{ sections }``).
- The body has the OpenAI ``model`` key (NOT the runtime's ``modelId`` field).
- Response parses from the OpenAI Chat Completions shape (``choices[0].message.content``).
"""
from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from vais_agent_sdk import AgentRequest

from sectioned_plugin_legacy.agent import invoke


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
    """Same three-section fixture the canonical sample uses, so the diff between the two
    tests is purely about the second call (plugin-side vs runtime-side flatten)."""
    return {
        "sections": [
            {
                "id": "system.persona",
                "kind": "SystemSegment",
                "payload": {"value": "You are a careful research assistant."},
                "producerId": "PersonaContributor",
            },
            {
                "id": "retrieval.docs",
                "kind": "SystemSegment",
                "payload": {"value": "Source 1: Returns within 30 days."},
                "producerId": "KnowledgeRetrievalContextProvider",
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


def _openai_chat_completion_response() -> dict[str, Any]:
    """Standard OpenAI Chat Completions response — what the runtime's
    /v1/container-gateway/chat/completions endpoint emits."""
    return {
        "id": "chatcmpl-test",
        "object": "chat.completion",
        "created": 0,
        "model": "gpt-4o-mini",
        "choices": [{
            "index": 0,
            "message": {"role": "assistant",
                        "content": "Returns are accepted within 30 days. Source 1."},
            "finish_reason": "stop",
        }],
        "usage": {"prompt_tokens": 42, "completion_tokens": 13, "total_tokens": 55},
    }


async def test_invoke_routes_through_chat_completions_with_plugin_side_flatten():
    captured: list[dict[str, Any]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        captured.append({
            "url": str(request.url),
            "headers": dict(request.headers),
            "body": json.loads(request.content),
        })
        if request.url.path == "/v1/container-gateway/sections/build":
            return httpx.Response(200, json=_sections_response())
        if request.url.path == "/v1/container-gateway/chat/completions":
            return httpx.Response(200, json=_openai_chat_completion_response())
        return httpx.Response(404, text=f"unhandled: {request.url}")

    client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    try:
        resp = await invoke(_build_request(), client=client)
    finally:
        await client.aclose()

    assert len(captured) == 2
    assert captured[0]["url"].endswith("/v1/container-gateway/sections/build")
    # Distinguishing assertion vs the canonical sample: SECOND call hits /chat/completions,
    # not /llm/complete. Same auth headers, different endpoint, different body shape.
    assert captured[1]["url"].endswith("/v1/container-gateway/chat/completions")

    for call in captured:
        assert call["headers"]["authorization"] == "Bearer tok-xyz"
        assert call["headers"]["x-run-id"] == "run-42"
        assert call["headers"]["x-agent-id"] == "research-helper"

    # The chat/completions body is fully flattened on the plugin side — no `sections`
    # field, just an OpenAI-shape `messages` array.
    chat_body = captured[1]["body"]
    assert "sections" not in chat_body
    assert "messages" in chat_body
    # The persona + retrieval SystemSegments concatenate (via the OpenAI adapter) into one
    # leading system message; then the user turn follows.
    assert chat_body["messages"] == [
        {"role": "system",
         "content": "You are a careful research assistant.\n\nSource 1: Returns within 30 days."},
        {"role": "user", "content": "What's our return policy?"},
    ]
    # OpenAI-shape `model` field (not the runtime's `modelId`).
    assert chat_body["model"] == "gpt-4o-mini"

    assert resp.assistant_message == "Returns are accepted within 30 days. Source 1."
    assert resp.usage is not None and len(resp.usage) == 1
    assert resp.usage[0].input_tokens == 42
    assert resp.usage[0].output_tokens == 13


async def test_invoke_propagates_404_from_unknown_agent():
    """Same error-propagation behaviour as the canonical sample — build_sections 404 raises
    httpx.HTTPStatusError so the plugin author sees the error and can fall back."""
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
