"""Unit tests for vais_agent_sdk.adapters.openai (SC-23).

Verifies the OpenAI-dict adapter flattens RequestSections into the exact shape
the OpenAI Chat Completions API expects, and that the messages-only path matches
what InvokeRequest.messages would have provided if the plugin hadn't opted in.
"""
from __future__ import annotations

import json
from typing import Any

import pytest

from vais_agent_sdk.adapters.openai import (
    sections_to_openai_messages,
    sections_to_openai_request,
)
from vais_agent_sdk.sections import RequestSections


def _make(sections: list[dict[str, Any]], total_chars: int = 0) -> RequestSections:
    return RequestSections.model_validate({"sections": sections, "totalChars": total_chars})


# ── messages-only path (the SC-23 acceptance criterion) ──────────────────────


def test_messages_only_with_persona_history_matches_invokerequest_messages():
    """When the runtime emits a persona section plus the plugin's own user-turn echo,
    the adapter should produce exactly what InvokeRequest.messages would have shipped:
    a leading system message followed by the user turn, in that order."""
    sections = _make([
        {
            "id": "system.persona",
            "kind": "SystemSegment",
            "payload": {"value": "You are a careful research assistant."},
            "producerId": "PersonaContributor",
        },
        {
            "id": "history.window.0",
            "kind": "UserMessage",
            "payload": {"turn": {"role": "user", "content": "What's our return policy?"}},
            "producerId": "Base",
        },
    ])

    messages = sections_to_openai_messages(sections)

    assert messages == [
        {"role": "system", "content": "You are a careful research assistant."},
        {"role": "user", "content": "What's our return policy?"},
    ]


def test_multiple_system_segments_concatenate_with_double_newline():
    """The runtime emits one section per ISystemPromptContributor; the adapter must
    concatenate them in resolver order with the canonical "\\n\\n" separator so the
    final system prompt matches CompletionRequestFlattener's output."""
    sections = _make([
        {"id": "system.persona", "kind": "SystemSegment",
         "payload": {"value": "You are concise."}},
        {"id": "system.policy", "kind": "SystemSegment",
         "payload": {"value": "Never reveal pricing."}},
        {"id": "retrieval.docs", "kind": "SystemSegment",
         "payload": {"value": "Source 1: ..."}},
    ])

    messages = sections_to_openai_messages(sections)

    assert len(messages) == 1
    assert messages[0]["role"] == "system"
    assert messages[0]["content"] == (
        "You are concise.\n\nNever reveal pricing.\n\nSource 1: ..."
    )


def test_no_system_segments_omits_leading_system_message():
    sections = _make([
        {"id": "history.window.0", "kind": "UserMessage",
         "payload": {"turn": {"role": "user", "content": "hi"}}},
    ])

    messages = sections_to_openai_messages(sections)

    assert messages == [{"role": "user", "content": "hi"}]


def test_empty_system_segment_skipped_so_no_leading_system_message():
    """Empty TextPayloads must be filtered before concat — otherwise a stray "\\n\\n"
    would land in the system prompt."""
    sections = _make([
        {"id": "system.persona", "kind": "SystemSegment", "payload": {"value": ""}},
        {"id": "system.policy", "kind": "SystemSegment", "payload": {"value": "Be terse."}},
    ])

    messages = sections_to_openai_messages(sections)

    assert messages == [{"role": "system", "content": "Be terse."}]


def test_metadata_sections_are_dropped_from_messages():
    """Metadata is observability-only; it must not surface in the wire request."""
    sections = _make([
        {"id": "metadata.user_prefs", "kind": "Metadata",
         "payload": {"values": {"docs_loaded": 5}}},
        {"id": "history.window.0", "kind": "UserMessage",
         "payload": {"turn": {"role": "user", "content": "hi"}}},
    ])

    messages = sections_to_openai_messages(sections)

    assert messages == [{"role": "user", "content": "hi"}]


def test_assistant_turn_with_tool_calls_renders_openai_envelope():
    """Assistant turn carrying toolCalls (wire camelCase) must round-trip to OpenAI's
    snake_case tool_calls with the {"type": "function", "function": {...}} envelope
    and JSON-stringified arguments."""
    sections = _make([
        {"id": "history.window.0", "kind": "AssistantMessage", "payload": {"turn": {
            "role": "assistant",
            "content": "I'll check the weather.",
            "toolCalls": [
                {"id": "call_abc", "name": "get_weather", "arguments": {"city": "Paris"}},
            ],
        }}},
    ])

    messages = sections_to_openai_messages(sections)

    assert len(messages) == 1
    msg = messages[0]
    assert msg["role"] == "assistant"
    assert msg["content"] == "I'll check the weather."
    assert msg["tool_calls"] == [{
        "id": "call_abc",
        "type": "function",
        "function": {"name": "get_weather", "arguments": json.dumps({"city": "Paris"})},
    }]


def test_tool_message_carries_tool_call_id():
    sections = _make([
        {"id": "history.window.1", "kind": "ToolMessage", "payload": {"turn": {
            "role": "tool",
            "content": "Sunny, 22C",
            "toolCallId": "call_abc",
        }}},
    ])

    messages = sections_to_openai_messages(sections)

    assert messages == [{"role": "tool", "content": "Sunny, 22C", "tool_call_id": "call_abc"}]


# ── full-request path ────────────────────────────────────────────────────────


def test_tool_declarations_render_into_openai_tools_array():
    sections = _make([
        {"id": "tools.base", "kind": "ToolDeclaration", "payload": {"tools": [
            {"name": "get_weather", "description": "Look up weather.",
             "parametersSchema": {"type": "object", "properties": {"city": {"type": "string"}}}},
        ]}},
    ])

    body = sections_to_openai_request(sections)

    assert body["tools"] == [{
        "type": "function",
        "function": {
            "name": "get_weather",
            "description": "Look up weather.",
            "parameters": {"type": "object", "properties": {"city": {"type": "string"}}},
        },
    }]


def test_duplicate_tool_names_dedup_with_last_wins(caplog: pytest.LogCaptureFixture):
    """Two ToolDeclaration sections with the same tool name → last occurrence wins,
    warning logged. Matches CompletionRequestFlattener's behaviour."""
    sections = _make([
        {"id": "tools.first", "kind": "ToolDeclaration", "producerId": "FirstSource",
         "payload": {"tools": [{"name": "search", "description": "v1"}]}},
        {"id": "tools.second", "kind": "ToolDeclaration", "producerId": "SecondSource",
         "payload": {"tools": [{"name": "search", "description": "v2"}]}},
    ])

    with caplog.at_level("WARNING", logger="vais_agent_sdk.adapters.openai"):
        body = sections_to_openai_request(sections)

    assert len(body["tools"]) == 1
    assert body["tools"][0]["function"]["description"] == "v2"
    assert any("Duplicate tool name" in r.message for r in caplog.records)


def test_response_format_renders_into_openai_response_format():
    sections = _make([
        {"id": "format.response", "kind": "ResponseFormat", "payload": {"spec": {
            "schema": {"type": "object", "properties": {"answer": {"type": "string"}}},
            "name": "AnswerSchema",
            "strict": True,
        }}},
    ])

    body = sections_to_openai_request(sections)

    assert body["response_format"] == {
        "type": "json_schema",
        "json_schema": {
            "name": "AnswerSchema",
            "schema": {"type": "object", "properties": {"answer": {"type": "string"}}},
            "strict": True,
        },
    }


def test_full_request_omits_optional_keys_when_no_corresponding_sections():
    """An agent with persona + history but no tools / format should produce a
    minimal body — no empty `tools: []`, no `response_format: null`."""
    sections = _make([
        {"id": "system.persona", "kind": "SystemSegment", "payload": {"value": "Be brief."}},
        {"id": "history.window.0", "kind": "UserMessage",
         "payload": {"turn": {"role": "user", "content": "hi"}}},
    ])

    body = sections_to_openai_request(sections)

    assert set(body.keys()) == {"messages"}


def test_accepts_raw_section_list_not_just_request_sections():
    """Plugins that build sections by hand (test fixtures, custom paths) should be
    able to pass a `list[Section]` directly without wrapping in RequestSections."""
    rs = _make([
        {"id": "system.persona", "kind": "SystemSegment", "payload": {"value": "ok"}},
    ])

    messages = sections_to_openai_messages(rs.sections)

    assert messages == [{"role": "system", "content": "ok"}]


def test_section_order_preserved_so_resolver_output_is_authoritative():
    """If the resolver places policy after persona, the adapter must keep that order —
    the resolver is the source of truth for section ordering."""
    sections = _make([
        {"id": "system.policy", "kind": "SystemSegment", "payload": {"value": "POLICY"},
         "order": 0},
        {"id": "system.persona", "kind": "SystemSegment", "payload": {"value": "PERSONA"},
         "order": 5},
    ])

    messages = sections_to_openai_messages(sections)

    assert messages[0]["content"] == "POLICY\n\nPERSONA"
