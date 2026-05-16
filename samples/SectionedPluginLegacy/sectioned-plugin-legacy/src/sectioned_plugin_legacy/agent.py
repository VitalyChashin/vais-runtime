"""SectionedAgentLegacy — plugin-side-flatten variant of SectionedAgent.

End-to-end flow per call:
1. Resolve the runtime gateway URL + auth from the inbound ``AgentRequest``.
2. Build the plugin's current ``messages`` view from the user message.
3. ``build_sections()`` — fetch the typed, resolver-ordered ``Section[]`` (same as the
   canonical sample).
4. (Optional, demonstration touch) print a one-line per-section breakdown.
5. ``sections_to_openai_request()`` — **flatten client-side** into the OpenAI Chat
   Completions wire shape. This is the key difference from the canonical sample.
6. POST the flattened body to ``/v1/container-gateway/chat/completions``.

Where this path is the right choice:
- The plugin is bridging a non-VAIS framework that expects the OpenAI chat-completions
  wire shape (any OpenAI-compatible SDK, custom clients that consume OpenAI-shape SSE).
- The plugin wants to drive the OpenAI-compat surface that external IDE / chat tools
  (OpenWebUI, LiteLLM, Continue.dev) also use.

Where this path is the **wrong** choice (and you should use the canonical sample):
- The plugin just wants per-section observability and the standard gateway + middleware
  behaviour. Use ``complete_from_sections`` from ``samples/SectionedPlugin/`` instead.

Observability trade-off (the operational difference between the two samples):
- The ``/v1/sections/build`` call fires its normal observability surface in both samples
  (per-section snapshot is built and emitted by sinks registered against the call's
  ``SectionTelemetryEmitter``).
- On THIS sample's path, the subsequent ``/chat/completions`` call lands in the runtime as
  a plain ``CompletionRequest`` — the gateway sees no section information. Per-section
  OTel section tags, Prometheus section histograms, Langfuse ``langfuse.section.*`` aliases,
  and the ``RequestSectionsBuilt`` event all stay silent for the LLM-call span on this
  path. Gateway-level telemetry (token counts, model id, Langfuse trace metadata,
  ``GatewayEventMiddleware`` rows) still fires normally — only the per-section attribution
  on the LLM-call span is lost.
- On the canonical sample's path, the runtime runs ``SectionTelemetryEmitter`` server-side
  before the LLM call, so every section sink fires the same as it would for a
  runtime-hosted agent.
"""
from __future__ import annotations

import logging
from typing import Any

import httpx

from vais_agent_sdk import (
    AgentRequest,
    AgentResponse,
    AgentUsage,
    RequestSections,
    build_sections,
    sections_to_openai_request,
)

logger = logging.getLogger(__name__)


async def invoke(
    request: AgentRequest,
    *,
    model: str = "gpt-4o-mini",
    client: httpx.AsyncClient | None = None,
) -> AgentResponse:
    """Handle one ``vais/agent.invoke`` call via the plugin-side-flatten path."""
    if client is None:
        async with httpx.AsyncClient(timeout=60.0) as fresh:
            return await _invoke_inner(request, model=model, client=fresh)
    return await _invoke_inner(request, model=model, client=client)


async def _invoke_inner(
    request: AgentRequest,
    *,
    model: str,
    client: httpx.AsyncClient,
) -> AgentResponse:
    gateway_url = request.llm_gateway_url
    call_token = request.call_token

    # 1+2. Plugin's view of the conversation.
    plugin_messages = [{"role": "user", "content": request.user_message}]

    # 3. Fetch the resolver-ordered Section[].
    sections: RequestSections = await build_sections(
        gateway_base_url=gateway_url,
        call_token=call_token,
        run_id=request.run_id,
        agent_id=request.agent_id,
        messages=plugin_messages,
        client=client,
    )

    # 4. Per-section breakdown — same observability touch as the canonical sample.
    _log_breakdown(sections)

    # 5. **Plugin-side flatten.** This is the difference from the canonical sample. The
    # adapter is intentionally a pure function — same flatten rules CompletionRequestFlattener
    # uses on the runtime side, so the wire output for the LLM is byte-equivalent. What
    # differs is WHERE the flatten happens — and therefore which side the
    # SectionTelemetryEmitter runs on. Here: it doesn't run at all on the LLM-call span.
    body = sections_to_openai_request(sections)
    body["model"] = model

    # 6. POST the flattened body to the OpenAI-compat endpoint. From this point on the
    # runtime treats the request like any other chat-completions call — the typed Section[]
    # is gone from the wire.
    completion = await _chat_completions(
        client=client,
        gateway_base_url=gateway_url,
        call_token=call_token,
        run_id=request.run_id,
        agent_id=request.agent_id,
        body=body,
    )

    reply, usage = _extract_reply(completion)

    return AgentResponse(
        assistantMessage=reply,
        usage=[AgentUsage(model=model, inputTokens=usage[0], outputTokens=usage[1])] if usage else None,
    )


def _log_breakdown(sections: RequestSections) -> None:
    """Mirror of the canonical sample's breakdown logger — kept identical so an operator
    comparing logs between the two samples sees the same per-section attribution on this
    path. The difference shows up in the LLM-call span's tags, not in the application log."""
    for s in sections.sections:
        chars = len(s.payload.value or "") if s.payload.value is not None else (
            len(s.payload.turn.get("content") or "") if s.payload.turn is not None else 0
        )
        logger.info(
            "section id=%s kind=%s producer=%s chars=%d order=%s priority=%s",
            s.id, s.kind, s.producer_id or "(none)", chars,
            s.order if s.order is not None else "(none)",
            s.budget.priority if s.budget else "(default)",
        )


async def _chat_completions(
    *,
    client: httpx.AsyncClient,
    gateway_base_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
    body: dict[str, Any],
) -> dict[str, Any]:
    url = f"{gateway_base_url.rstrip('/')}/v1/container-gateway/chat/completions"
    headers = {
        "Authorization": f"Bearer {call_token}",
        "X-Run-Id": run_id,
        "X-Agent-Id": agent_id,
    }
    resp = await client.post(url, headers=headers, json=body)
    resp.raise_for_status()
    return resp.json()


def _extract_reply(completion: dict[str, Any]) -> tuple[str, tuple[int, int] | None]:
    choices = completion.get("choices") or []
    if not choices:
        return "", None
    message = choices[0].get("message") or {}
    content = message.get("content") or ""

    usage_dict = completion.get("usage") or {}
    pt = usage_dict.get("prompt_tokens")
    ct = usage_dict.get("completion_tokens")
    usage = (int(pt), int(ct)) if pt is not None and ct is not None else None

    return content, usage
