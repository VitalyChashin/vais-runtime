"""SectionedAgent — opt-in to the runtime's section pipeline via the canonical path.

End-to-end flow per call:
1. Resolve the runtime gateway URL + auth from the inbound ``AgentRequest``.
2. Build the plugin's current ``messages`` view from the user message (and any carried-over
   state if the plugin chose to maintain it).
3. ``build_sections()`` — fetch the typed, resolver-ordered ``Section[]`` the runtime would
   normally have flattened internally.
4. (Optional, demonstration touch) print a one-line per-section breakdown so an operator can
   see the composition decision live; in production a plugin might mutate sections here
   (drop a noisy producer, override budgets, suppress metadata).
5. ``complete_from_sections()`` — ship the (possibly mutated) section list **back** to the
   runtime so it runs the canonical pipeline server-side: resolver re-validates, packer
   applies the agent's budget, the telemetry emitter fires (so OTel / Langfuse / Prometheus /
   ``RequestSectionsBuilt`` event all see the per-section breakdown), the flattener produces
   the final ``CompletionRequest``, and the LLM gateway middleware chain runs. The plugin
   receives the assistant reply + usage totals.

This is the v0.27 path. It restores **telemetry symmetry** with runtime-hosted agents — the
plugin gets per-section observability for free instead of having to reimplement it. The
older flatten-in-the-plugin shape (using :mod:`vais_agent_sdk.adapters.openai` + a direct
call to ``/v1/container-gateway/chat/completions``) is still supported for backwards
compatibility and for plugins integrating with non-VAIS toolchains — see the companion
sample at ``samples/SectionedPluginLegacy/`` for the contrast.
"""
from __future__ import annotations

import logging

import httpx

from vais_agent_sdk import (
    AgentRequest,
    AgentResponse,
    AgentUsage,
    RequestSections,
    build_sections,
    complete_from_sections,
)

logger = logging.getLogger(__name__)


async def invoke(
    request: AgentRequest,
    *,
    model: str = "gpt-4o-mini",
    client: httpx.AsyncClient | None = None,
) -> AgentResponse:
    """Handle one ``vais/agent.invoke`` call end-to-end via the canonical v0.27 path."""
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

    # 1+2. Plugin's view of the conversation. The plugin owns this on the v0.27 path; the
    # runtime treats it as the candidate the providers see.
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

    # 4. One-line per-section breakdown — operator visibility into composition. A production
    # plugin could also mutate sections here before sending them back.
    _log_breakdown(sections)

    # 5. Ship the sections back. Runtime runs the canonical flatten + telemetry + LLM call.
    result = await complete_from_sections(
        gateway_base_url=gateway_url,
        call_token=call_token,
        run_id=request.run_id,
        agent_id=request.agent_id,
        sections=sections,
        model_id=model,
        client=client,
    )

    return AgentResponse(
        assistantMessage=result.message.get("content", ""),
        usage=[AgentUsage(
            model=model,
            inputTokens=result.usage.input_tokens,
            outputTokens=result.usage.output_tokens,
        )] if result.usage is not None else None,
    )


def _log_breakdown(sections: RequestSections) -> None:
    """Print one log line per section so operators can attribute context usage."""
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


# The pre-v0.27 plugin-side-flatten shape lives as its own complete sample at
# samples/SectionedPluginLegacy/ — kept there rather than inline here so the contrast
# between the two paths is visible at the sample/file level (one sample per path).
