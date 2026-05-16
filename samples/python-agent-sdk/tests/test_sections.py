"""Unit tests for vais_agent_sdk.sections (SC-22).

Exercises the build_sections() client end-to-end against an in-memory
httpx.MockTransport — no live server required. Verifies wire serialisation,
typed response parsing, error mapping, and that adapters can dispatch on
section.kind correctly.
"""
from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from vais_agent_sdk.sections import (
    RequestSections,
    Section,
    SectionPayload,
    build_sections,
)


def _fake_response(sections: list[dict[str, Any]], total_chars: int = 0) -> dict[str, Any]:
    return {"sections": sections, "totalChars": total_chars}


def _make_client(handler) -> httpx.AsyncClient:
    transport = httpx.MockTransport(handler)
    return httpx.AsyncClient(transport=transport, base_url="http://gateway.local")


async def test_build_sections_posts_correct_url_and_headers():
    captured: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["headers"] = dict(request.headers)
        captured["body"] = json.loads(request.content)
        return httpx.Response(200, json=_fake_response([], total_chars=0))

    client = _make_client(handler)
    try:
        result = await build_sections(
            gateway_base_url="http://gateway.local",
            call_token="tok-123",
            run_id="run-42",
            agent_id="agent-x",
            messages=[{"role": "user", "content": "hi"}],
            client=client,
        )
    finally:
        await client.aclose()

    assert captured["url"] == "http://gateway.local/v1/container-gateway/sections/build"
    assert captured["headers"]["authorization"] == "Bearer tok-123"
    assert captured["headers"]["x-run-id"] == "run-42"
    assert captured["headers"]["x-agent-id"] == "agent-x"
    assert captured["body"] == {"messages": [{"role": "user", "content": "hi"}]}
    assert isinstance(result, RequestSections)
    assert result.sections == []
    assert result.total_chars == 0


async def test_build_sections_parses_typed_payloads():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=_fake_response([
            {
                "id": "system.persona",
                "kind": "SystemSegment",
                "payload": {"value": "You are a careful research assistant."},
                "order": 0,
                "producerId": "PersonaContributor",
                "budget": {"priority": 0},
            },
            {
                "id": "retrieval.docs",
                "kind": "SystemSegment",
                "payload": {"value": "Source 1: ..."},
                "producerId": "KnowledgeRetrievalContextProvider",
                "budget": {"priority": 5, "maxChars": 2000},
            },
            {
                "id": "history.window.0",
                "kind": "UserMessage",
                "payload": {"turn": {"role": "user", "content": "What's our return policy?"}},
                "producerId": "Base",
            },
        ], total_chars=482))

    client = _make_client(handler)
    try:
        result = await build_sections(
            gateway_base_url="http://gateway.local",
            call_token="t", run_id="r", agent_id="a",
            messages=[],
            client=client,
        )
    finally:
        await client.aclose()

    assert result.total_chars == 482
    assert len(result.sections) == 3

    persona = result.sections[0]
    assert persona.id == "system.persona"
    assert persona.kind == "SystemSegment"
    assert persona.payload.value == "You are a careful research assistant."
    assert persona.producer_id == "PersonaContributor"
    assert persona.budget is not None and persona.budget.priority == 0

    retrieval = result.sections[1]
    assert retrieval.budget is not None
    assert retrieval.budget.max_chars == 2000

    history = result.sections[2]
    assert history.kind == "UserMessage"
    assert history.payload.turn == {"role": "user", "content": "What's our return policy?"}
    assert history.payload.value is None


async def test_build_sections_404_raises_http_status_error():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"title": "Agent 'ghost' not found."})

    client = _make_client(handler)
    try:
        with pytest.raises(httpx.HTTPStatusError) as exc:
            await build_sections(
                gateway_base_url="http://gateway.local",
                call_token="t", run_id="r", agent_id="ghost",
                messages=[],
                client=client,
            )
        assert exc.value.response.status_code == 404
    finally:
        await client.aclose()


async def test_build_sections_500_carries_producer_id_extension():
    """Provider failures surface as 500 with a producerId extension that the
    plugin can read to attribute the fault."""
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, json={
            "title": "Context provider 'BrokenRetriever' failed.",
            "producerId": "BrokenRetriever",
        })

    client = _make_client(handler)
    try:
        with pytest.raises(httpx.HTTPStatusError) as exc:
            await build_sections(
                gateway_base_url="http://gateway.local",
                call_token="t", run_id="r", agent_id="a",
                messages=[],
                client=client,
            )
        assert exc.value.response.status_code == 500
        assert exc.value.response.json()["producerId"] == "BrokenRetriever"
    finally:
        await client.aclose()


async def test_build_sections_strips_trailing_slash_from_base_url():
    captured: dict[str, str] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        return httpx.Response(200, json=_fake_response([]))

    # Doubled slash in the base URL would break some routers; the helper
    # should normalise.
    client = _make_client(handler)
    try:
        await build_sections(
            gateway_base_url="http://gateway.local/",
            call_token="t", run_id="r", agent_id="a",
            messages=[],
            client=client,
        )
    finally:
        await client.aclose()

    assert captured["url"] == "http://gateway.local/v1/container-gateway/sections/build"


def test_section_payload_can_carry_metadata_values():
    """Metadata sections never flatten to the wire but the typed response still
    parses them — observability adapters surface the bag."""
    payload = SectionPayload.model_validate({
        "values": {"docs_loaded": 7, "scope": "user.long"},
    })
    assert payload.values == {"docs_loaded": 7, "scope": "user.long"}
    assert payload.value is None and payload.turn is None and payload.tools is None
