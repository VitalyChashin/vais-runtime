"""Integration test for the SectionedAgent end-to-end flow (SC-24).

Drives one ``invoke()`` call against an in-memory httpx.MockTransport that stands in
for the runtime gateway. Verifies that the plugin:

1. Calls /v1/sections/build with the right URL + headers + plugin's messages view.
2. Flattens the returned Section[] via sections_to_openai_request().
3. POSTs the flattened body to /v1/container-gateway/chat/completions with the same
   auth headers and model field set.
4. Returns the assistant content + usage in an AgentResponse.

No live runtime required — the test is the smoke check the SC-24 acceptance asks
for, plus a deterministic regression guard against future plugin / SDK drift.
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


def _completion_response() -> dict[str, Any]:
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


async def test_invoke_end_to_end_routes_through_sections_build_and_chat_completions():
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
        if request.url.path == "/v1/container-gateway/chat/completions":
            return httpx.Response(200, json=_completion_response())
        return httpx.Response(404, text=f"unhandled: {request.url}")

    client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    try:
        resp = await invoke(_build_request(), client=client)
    finally:
        await client.aclose()

    # Two calls, in order: sections/build then chat/completions.
    assert len(captured) == 2
    assert captured[0]["url"].endswith("/v1/container-gateway/sections/build")
    assert captured[1]["url"].endswith("/v1/container-gateway/chat/completions")

    # Both calls carry the bearer + correlation headers.
    for call in captured:
        assert call["headers"]["authorization"] == "Bearer tok-xyz"
        assert call["headers"]["x-run-id"] == "run-42"
        assert call["headers"]["x-agent-id"] == "research-helper"

    # sections/build receives the plugin's messages view.
    assert captured[0]["body"] == {
        "messages": [{"role": "user", "content": "What's our return policy?"}],
    }

    # chat/completions body shape — the OpenAI adapter renders persona+retrieval into a
    # single concatenated system message, then the user turn.
    completion_body = captured[1]["body"]
    assert completion_body["model"] == "gpt-4o-mini"
    assert completion_body["messages"] == [
        {"role": "system",
         "content": "You are a careful research assistant.\n\nSource 1: Returns within 30 days."},
        {"role": "user", "content": "What's our return policy?"},
    ]
    # No tools / response_format sections in this fixture, so those keys are omitted.
    assert "tools" not in completion_body
    assert "response_format" not in completion_body

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
