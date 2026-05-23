"""POST /v1/container-gateway/sections/build client (SC-22).

A Python container plugin opts into the typed Section[] view of per-turn context
by calling :func:`build_sections` with its current messages view. The runtime runs
the named agent's section pipeline (composer + IContextProvider chain + resolver)
and returns the resolver-ordered section list.

This is purely additive: the default plugin path (consume ``InvokeRequest.messages``)
keeps working. Plugins opt in when they want per-producer attribution, RAG
visibility, or to drive a framework-native layout (see ``adapters/openai.py``
for the simplest flatten back to a chat-completions message list).

Contract: ``contracts/plugin-container/gateway-internal.md`` v0.26.
"""
from __future__ import annotations

from typing import Any, Optional

import httpx
from pydantic import BaseModel, Field

from vais_agent_sdk._errors import LlmGatewayError


class SectionBudget(BaseModel):
    """Optional per-section budget hint. Priority 0 = critical, 10 = drop first."""

    priority: int
    max_chars: Optional[int] = Field(default=None, alias="maxChars")

    model_config = {"populate_by_name": True}


class SectionPayload(BaseModel):
    """Discriminated by the parent :class:`Section`'s ``kind`` field.

    Exactly one of these fields is populated per section; the rest are None.
    Adapters dispatch on the parent ``kind`` and read the matching field.
    """

    value: Optional[str] = None
    """Populated for ``SystemSegment`` sections."""

    turn: Optional[dict[str, Any]] = None
    """Populated for ``UserMessage`` / ``AssistantMessage`` / ``ToolMessage`` sections.

    Shape: ``{"role": "...", "content": "...", "toolCalls": [...], "toolCallId": "..."}``
    (the wire ``Message`` type from plugin-protocol.md).
    """

    tools: Optional[list[dict[str, Any]]] = None
    """Populated for ``ToolDeclaration`` sections.

    Each entry: ``{"name": "...", "description": "...", "parametersSchema": {...}}``.
    """

    spec: Optional[dict[str, Any]] = None
    """Populated for ``ResponseFormat`` sections.

    Shape: ``{"schema": {...}, "name": "...", "strict": True}``.
    """

    values: Optional[dict[str, Any]] = None
    """Populated for ``Metadata`` sections (observability-only — never flatten)."""


class Section(BaseModel):
    """One typed contribution to the LLM request, resolver-ordered."""

    id: str
    kind: str
    """One of ``SystemSegment``, ``UserMessage``, ``AssistantMessage``,
    ``ToolMessage``, ``ToolDeclaration``, ``ResponseFormat``, ``Metadata``."""

    payload: SectionPayload
    order: Optional[int] = None
    producer_id: Optional[str] = Field(default=None, alias="producerId")
    budget: Optional[SectionBudget] = None

    model_config = {"populate_by_name": True}


class RequestSections(BaseModel):
    """Typed wrapper for the ``/v1/container-gateway/sections/build`` response."""

    sections: list[Section] = Field(default_factory=list)
    total_chars: int = Field(default=0, alias="totalChars")

    model_config = {"populate_by_name": True}


async def build_sections(
    *,
    gateway_base_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
    messages: list[dict[str, Any]],
    timeout_seconds: float = 30.0,
    client: Optional[httpx.AsyncClient] = None,
) -> RequestSections:
    """Call ``POST /v1/container-gateway/sections/build`` and parse the response.

    Parameters
    ----------
    gateway_base_url
        Container-gateway base URL — typically ``InvokeRequest.context["llmGatewayUrl"]``
        (same host the LLM / tool callbacks ride on).
    call_token
        Bearer token from ``InvokeRequest.context["callToken"]``.
    run_id, agent_id
        Run + agent identifiers used to reconstruct the runtime-side
        ``AgentContext`` and authenticate the call.
    messages
        The plugin's current conversation view. Passed to the runtime as the
        candidate request — retrieval providers read the last user turn here
        for their queries. Each entry follows the wire ``Message`` shape:
        ``{"role": "user", "content": "..."}``.
    timeout_seconds
        Request timeout. Defaults to 30 seconds.
    client
        Optional pre-constructed ``httpx.AsyncClient``. When None, a fresh
        client is built for the duration of the call and closed on exit.

    Returns
    -------
    RequestSections
        Resolver-ordered section list plus ``total_chars`` aggregate.

    Raises
    ------
    httpx.HTTPStatusError
        Non-2xx response. 404 = unknown agent, 500 = provider failure
        (response body carries a ``producerId`` extension), 401 = bad token.
        Plugins should typically catch and fall back to ``InvokeRequest.messages``.
    """
    headers = {
        "Authorization": f"Bearer {call_token}",
        "X-Run-Id": run_id,
        "X-Agent-Id": agent_id,
    }
    url = f"{gateway_base_url.rstrip('/')}/v1/container-gateway/sections/build"
    payload = {"messages": messages}

    if client is None:
        async with httpx.AsyncClient(timeout=timeout_seconds) as fresh:
            resp = await fresh.post(url, headers=headers, json=payload)
    else:
        resp = await client.post(url, headers=headers, json=payload, timeout=timeout_seconds)

    # build_sections is the optional context-shaping helper — plugins typically catch and fall back to
    # InvokeRequest.messages, so a failure here stays an httpx.HTTPStatusError rather than auto-raising
    # LlmGatewayError. The actual LLM call (complete_from_sections) raises LlmGatewayError on non-2xx.
    resp.raise_for_status()
    return RequestSections.model_validate(resp.json())


# ── complete_from_sections (contract v0.27) ─────────────────────────────────


class CompletionUsage(BaseModel):
    """Token-usage counts returned by the runtime."""

    input_tokens: int = Field(default=0, alias="inputTokens")
    output_tokens: int = Field(default=0, alias="outputTokens")
    cached_tokens: int = Field(default=0, alias="cachedTokens")

    model_config = {"populate_by_name": True}


class CompletionResult(BaseModel):
    """Typed response from :func:`complete_from_sections` (non-streaming)."""

    message: dict[str, Any]
    """Wire ``Message`` shape — ``{"role": "assistant", "content": "..."}``."""

    usage: Optional[CompletionUsage] = None


async def complete_from_sections(
    *,
    gateway_base_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
    sections: RequestSections | list[Section],
    model_id: Optional[str] = None,
    temperature: Optional[float] = None,
    max_tokens: Optional[int] = None,
    timeout_seconds: float = 60.0,
    client: Optional[httpx.AsyncClient] = None,
) -> CompletionResult:
    """Send a ``Section[]`` body to ``POST /v1/container-gateway/llm/complete``.

    This is the **canonical** path for plugins that opted into the section pipeline via
    :func:`build_sections`. The runtime runs the full pipeline server-side — resolver +
    packer + telemetry emitter + flattener — so per-section telemetry (OTel tags, Prometheus
    metrics, Langfuse enrichment, the ``RequestSectionsBuilt`` event, structured logs) fires
    the same way it does for a runtime-hosted agent. The plugin does *not* need to flatten
    the section list itself — that's what the legacy adapter path was for, and it had a
    telemetry-loss regression that this endpoint fixes.

    Backwards compatibility: the same endpoint still accepts ``{ messages: [...] }`` via the
    older :func:`complete_messages` shape (or via :mod:`vais_agent_sdk.adapters.openai` +
    direct ``httpx.post`` — see the SectionedPlugin sample for both forms side-by-side).

    Parameters
    ----------
    gateway_base_url
        Container-gateway base URL.
    call_token, run_id, agent_id
        Standard auth + correlation triple.
    sections
        Either the :class:`RequestSections` returned by :func:`build_sections`, or a raw
        list of :class:`Section` objects. The same instance is round-tripped verbatim by
        default; the plugin can drop, reorder, or edit sections client-side before sending.
    model_id
        Optional model id override; null defers to the agent's manifest-configured default.
    temperature, max_tokens
        Optional sampling hints forwarded into ``CompletionRequest`` server-side.
    timeout_seconds
        HTTP timeout. Defaults to 60 seconds (typical LLM call window).
    client
        Optional pre-constructed ``httpx.AsyncClient`` (e.g. supplied by a test).

    Returns
    -------
    CompletionResult
        ``message`` (the assistant turn) + optional ``usage`` totals.

    Raises
    ------
    LlmGatewayError
        Non-2xx response (the status code and response body are included in the message). 400 =
        malformed sections or both/neither body fields; 404 = unknown agent; 500 = provider failure
        (body carries a ``producerId`` Problem-Details extension).
    """
    section_list = sections.sections if isinstance(sections, RequestSections) else list(sections)
    wire_sections = [s.model_dump(by_alias=True, exclude_none=True) for s in section_list]
    body: dict[str, Any] = {"sections": wire_sections}
    if model_id is not None:
        body["modelId"] = model_id
    options: dict[str, Any] = {}
    if temperature is not None:
        options["temperature"] = temperature
    if max_tokens is not None:
        options["maxTokens"] = max_tokens
    if options:
        body["options"] = options

    headers = {
        "Authorization": f"Bearer {call_token}",
        "X-Run-Id": run_id,
        "X-Agent-Id": agent_id,
    }
    url = f"{gateway_base_url.rstrip('/')}/v1/container-gateway/llm/complete"

    if client is None:
        async with httpx.AsyncClient(timeout=timeout_seconds) as fresh:
            resp = await fresh.post(url, headers=headers, json=body)
    else:
        resp = await client.post(url, headers=headers, json=body, timeout=timeout_seconds)

    if resp.status_code >= 400:
        raise LlmGatewayError(f"LLM gateway llm/complete returned HTTP {resp.status_code}: {resp.text[:500]}")
    return CompletionResult.model_validate(resp.json())
