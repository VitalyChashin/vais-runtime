"""SectionedAgent — drives one turn through /v1/sections/build + the OpenAI adapter.

End-to-end flow per call:
1. Resolve the runtime gateway URL + auth from the inbound ``AgentRequest``.
2. Build a one-shot ``messages`` view from the user message (plus optional carried-over
   state, if the plugin chose to maintain it).
3. POST to ``/v1/container-gateway/sections/build`` to fetch the resolver-ordered Section[]
   the runtime would normally have flattened internally.
4. (Demo touch) print a one-line per-section breakdown so an operator can see the
   composition decision being made.
5. Flatten via ``sections_to_openai_request()`` and POST to the runtime's
   ``/v1/container-gateway/chat/completions`` to get the assistant reply.
6. Return the reply as an ``AgentResponse``.

The plugin holds no extra state beyond the conversation history that the runtime owns.
A richer plugin would also serialise per-section telemetry into its own opaque state.
"""
from __future__ import annotations

import json
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
    """Handle one ``vais/agent.invoke`` call end-to-end.

    Parameters
    ----------
    request
        The incoming JSON-RPC request, carrying the gateway URL + call token in its
        context block.
    model
        OpenAI model identifier forwarded into the chat-completions body. Override
        per agent if needed.
    client
        Optional pre-constructed ``httpx.AsyncClient`` (e.g. supplied by a test). When
        None, a fresh client is built for the duration of the call.
    """
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

    # 1+2. Plugin's view of the conversation. The runtime would also have this from its
    # session store, but the plugin's view is authoritative on this path (contract v0.26).
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

    # 4. One-line per-section breakdown — operator visibility into composition.
    _log_breakdown(sections)

    # 5. Flatten + call back through the gateway. response_format is included automatically
    # if the runtime emitted a ResponseFormat section.
    request_body = sections_to_openai_request(sections)
    request_body["model"] = model

    completion = await _chat_completions(
        client=client,
        gateway_base_url=gateway_url,
        call_token=call_token,
        run_id=request.run_id,
        agent_id=request.agent_id,
        body=request_body,
    )

    reply, usage = _extract_reply(completion)

    return AgentResponse(
        assistantMessage=reply,
        usage=[AgentUsage(model=model, inputTokens=usage[0], outputTokens=usage[1])] if usage else None,
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
