"""Reference OpenAI-dict adapter for ``RequestSections`` (SC-23).

The simplest adapter — flattens a resolver-ordered :class:`~vais_agent_sdk.sections.RequestSections`
into the exact list of messages a plugin would otherwise receive via
``InvokeRequest.messages``. Effectively reproduces today's default behaviour, but the plugin
author can inspect / mutate sections first and decide which to keep.

The flatten rules mirror the runtime-side ``CompletionRequestFlattener``:

* ``SystemSegment`` payloads concatenated with ``"\\n\\n"`` into one leading ``{"role": "system"}``
  message; empty values are skipped.
* Turn-shaped sections (``UserMessage`` / ``AssistantMessage`` / ``ToolMessage``) collected in
  resolver order into the messages list.
* ``ToolDeclaration`` payloads deduplicated by ``name`` (last wins; warning logged) and rendered
  into the OpenAI ``tools`` array (``{"type": "function", "function": {...}}``).
* ``ResponseFormat`` payload rendered into the OpenAI ``response_format`` object
  (``{"type": "json_schema", "json_schema": {...}}``).
* ``Metadata`` sections never flatten to the wire — skipped.

Two entry points:

* :func:`sections_to_openai_messages` — just the ``messages`` list (the simplest path; satisfies
  the SC-23 acceptance criterion that adapter output equals ``InvokeRequest.messages`` for an
  equivalent input).
* :func:`sections_to_openai_request` — the full ``{"messages": [...], "tools": [...],
  "response_format": {...}}`` dict, ready to pass straight to the OpenAI chat-completions API
  (or via the gateway-routed ``ChatOpenAI`` in :mod:`vais_agent_sdk.langchain_compat`).
"""
from __future__ import annotations

import logging
from typing import Any

from vais_agent_sdk.sections import RequestSections, Section

logger = logging.getLogger(__name__)

# Section.kind values we treat as turn-shaped. Keeping them as a literal set makes the dispatch
# fast and avoids importing an Enum that doesn't exist on the wire.
_TURN_KINDS = {"UserMessage", "AssistantMessage", "ToolMessage"}
_ROLE_BY_KIND = {
    "UserMessage": "user",
    "AssistantMessage": "assistant",
    "ToolMessage": "tool",
}


def sections_to_openai_messages(sections: RequestSections | list[Section]) -> list[dict[str, Any]]:
    """Flatten a section list into the OpenAI chat-completions ``messages`` array.

    The output matches what the runtime would have shipped as ``InvokeRequest.messages``
    if the plugin hadn't opted into the section pipeline — same order, same shape.

    Parameters
    ----------
    sections
        Either a :class:`RequestSections` (response from :func:`build_sections`) or a raw
        list of :class:`Section` objects.

    Returns
    -------
    list[dict[str, Any]]
        Messages ready to drop into the OpenAI Chat Completions request body. Includes a
        leading system message when any ``SystemSegment`` carries non-empty text.
    """
    section_list = _coerce_sections(sections)

    messages: list[dict[str, Any]] = []

    system_text = _build_system_text(section_list)
    if system_text is not None:
        messages.append({"role": "system", "content": system_text})

    for s in section_list:
        if s.kind not in _TURN_KINDS:
            continue
        turn = s.payload.turn
        if turn is None:
            # Defensive: a turn-kind section with no payload.turn is malformed; skip rather
            # than emit a stub message that breaks the model call.
            logger.warning(
                "Section '%s' has kind=%s but payload.turn is None — skipping.",
                s.id, s.kind,
            )
            continue
        messages.append(_render_turn(s.kind, turn))

    return messages


def sections_to_openai_request(sections: RequestSections | list[Section]) -> dict[str, Any]:
    """Flatten a section list into a full OpenAI chat-completions request body.

    Returns the ``{"messages": [...], "tools": [...], "response_format": {...}}`` dict that
    OpenAI's ``/v1/chat/completions`` accepts. Tools and response_format keys are omitted
    when no corresponding sections are present.

    Parameters
    ----------
    sections
        Either a :class:`RequestSections` or a raw list of :class:`Section` objects.
    """
    section_list = _coerce_sections(sections)

    body: dict[str, Any] = {"messages": sections_to_openai_messages(section_list)}

    tools = _build_tools(section_list)
    if tools:
        body["tools"] = tools

    response_format = _build_response_format(section_list)
    if response_format is not None:
        body["response_format"] = response_format

    return body


# ── internal helpers ────────────────────────────────────────────────────────


def _coerce_sections(sections: RequestSections | list[Section]) -> list[Section]:
    if isinstance(sections, RequestSections):
        return list(sections.sections)
    return list(sections)


def _build_system_text(sections: list[Section]) -> str | None:
    parts: list[str] = []
    for s in sections:
        if s.kind != "SystemSegment":
            continue
        text = s.payload.value
        if text:
            parts.append(text)
    if not parts:
        return None
    return "\n\n".join(parts)


def _render_turn(kind: str, turn: dict[str, Any]) -> dict[str, Any]:
    """Map a section's TurnPayload (wire ``Message`` shape) onto the OpenAI message shape.

    The wire shape and the OpenAI shape mostly overlap; the two non-trivial pieces are
    ``toolCalls`` (camelCase) → ``tool_calls`` (snake_case, with the OpenAI ``{"type":
    "function", "function": {...}}`` envelope) and ``toolCallId`` → ``tool_call_id``.
    """
    role = _ROLE_BY_KIND.get(kind, "user")
    msg: dict[str, Any] = {"role": role, "content": turn.get("content")}

    tool_call_id = turn.get("toolCallId") or turn.get("tool_call_id")
    if tool_call_id:
        msg["tool_call_id"] = tool_call_id

    tool_calls = turn.get("toolCalls") or turn.get("tool_calls")
    if tool_calls:
        msg["tool_calls"] = [_render_tool_call(tc) for tc in tool_calls]

    return msg


def _render_tool_call(tc: dict[str, Any]) -> dict[str, Any]:
    """Wire ``PluginToolCall`` → OpenAI ``tool_calls[]`` entry."""
    import json

    args = tc.get("arguments", {})
    # OpenAI sends function.arguments as a JSON-encoded string, not a nested object.
    # Round-trip dict / list / scalar through json.dumps to preserve that shape.
    args_str = args if isinstance(args, str) else json.dumps(args)

    return {
        "id": tc.get("id", ""),
        "type": "function",
        "function": {
            "name": tc.get("name", ""),
            "arguments": args_str,
        },
    }


def _build_tools(sections: list[Section]) -> list[dict[str, Any]]:
    """Collect ToolDeclaration sections, dedup by tool name (last wins).

    Returns the OpenAI ``tools`` array shape: a list of ``{"type": "function", "function":
    {"name": ..., "description": ..., "parameters": ...}}`` entries.
    """
    by_name: dict[str, dict[str, Any]] = {}
    order: list[str] = []

    for s in sections:
        if s.kind != "ToolDeclaration":
            continue
        tools = s.payload.tools or []
        for t in tools:
            name = t.get("name", "")
            if not name:
                continue
            if name in by_name:
                logger.warning(
                    "Duplicate tool name %r in section %r (producer %r); replacing earlier occurrence.",
                    name, s.id, s.producer_id or "",
                )
            else:
                order.append(name)
            by_name[name] = {
                "type": "function",
                "function": {
                    "name": name,
                    "description": t.get("description", ""),
                    "parameters": t.get("parametersSchema") or t.get("parameters") or {},
                },
            }

    return [by_name[n] for n in order]


def _build_response_format(sections: list[Section]) -> dict[str, Any] | None:
    """Render the (at most one) ResponseFormat section into OpenAI's ``response_format``."""
    first: dict[str, Any] | None = None
    first_id: str | None = None

    for s in sections:
        if s.kind != "ResponseFormat":
            continue
        spec = s.payload.spec
        if spec is None:
            continue
        if first is None:
            first = {
                "type": "json_schema",
                "json_schema": {
                    "name": spec.get("name") or "response",
                    "schema": spec.get("schema") or {},
                    "strict": bool(spec.get("strict", True)),
                },
            }
            first_id = s.id
        else:
            logger.warning(
                "Multiple ResponseFormat sections passed to adapter (first %r, also %r); keeping the first.",
                first_id, s.id,
            )

    return first
