"""Gateway clients for LLM completions and tool invocations."""

from __future__ import annotations

import asyncio
import dataclasses
from dataclasses import dataclass, field
from typing import Any, AsyncIterator

import httpx

from ._tokens import TokenManager
from .models import LlmGatewayError, Message, RequestContext, ToolCall, ToolError, UsageCounts


async def _send(base_url: str, path: str, body: dict[str, Any], tokens: TokenManager) -> httpx.Response:
    """POSTs ``body`` with the manager's auth headers, retrying once after a 401 with a fresh token."""
    token_used = tokens.current_token
    async with httpx.AsyncClient(base_url=base_url) as client:
        resp = await client.post(path, json=body, headers=await tokens.auth_headers())
        if resp.status_code == 401 and tokens.can_renew:
            await tokens.refresh_if_stale(token_used)
            resp = await client.post(path, json=body, headers=await tokens.auth_headers())
    return resp


@dataclass
class LlmResponse:
    """Response from a successful LLM gateway completion call."""

    content: str | None
    tool_calls: list[ToolCall] = field(default_factory=list)
    usage: UsageCounts | None = None

    def as_message(self) -> Message:
        """Converts this response to a Message for appending to the conversation."""
        return Message(role="assistant", content=self.content, tool_calls=self.tool_calls)


@dataclass
class ToolResult:
    """Result of a single tool invocation."""

    tool_call_id: str
    content: str
    is_error: bool = False


class AsyncLlmClient:
    """Pre-configured LLM gateway client.

    Every outbound request carries ``Authorization``, ``X-Agent-Id``,
    ``X-Run-Id``, and ``traceparent`` headers.  Use this client exclusively;
    direct LLM calls violate architectural principle P4.
    """

    def __init__(
        self,
        llm_gateway_url: str,
        context: RequestContext,
        agent_id: str,
        tokens: TokenManager | None = None,
    ) -> None:
        self._base_url = llm_gateway_url.rstrip("/") + "/"
        self._agent_id = agent_id
        # Share a TokenManager across the LLM + tool clients when the caller provides one; otherwise
        # build a per-client manager (pass-through unless context.renew_url is set).
        self._tokens = tokens or TokenManager(context, agent_id)

    async def complete(
        self,
        messages: list[Message],
        *,
        model_id: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
    ) -> LlmResponse:
        """Sends a completion request and returns the full response."""
        body = _serialise_llm_request(messages, model_id, temperature, max_tokens)
        resp = await _send(self._base_url, "complete", body, self._tokens)
        if resp.status_code >= 400:
            raise LlmGatewayError(
                f"LLM gateway returned HTTP {resp.status_code}: {resp.text[:500]}"
            )
        data = resp.json()
        msg = data.get("message") or {}
        usage = data.get("usage")
        return LlmResponse(
            content=msg.get("content"),
            tool_calls=[
                ToolCall(id=tc["id"], name=tc["name"], arguments=tc.get("arguments", {}))
                for tc in (msg.get("toolCalls") or [])
            ],
            usage=UsageCounts(
                input_tokens=usage.get("inputTokens", 0),
                output_tokens=usage.get("outputTokens", 0),
                cached_tokens=usage.get("cachedTokens", 0),
            ) if usage else None,
        )

    async def stream(
        self,
        messages: list[Message],
        *,
        model_id: str | None = None,
        temperature: float | None = None,
        max_tokens: int | None = None,
    ) -> AsyncIterator[str]:
        """Streams response tokens as strings.

        IP-2 implementation: delegates to :meth:`complete` and yields the content
        as a single token.  True SSE streaming from the gateway is wired in IP-3.
        """
        response = await self.complete(messages, model_id=model_id, temperature=temperature, max_tokens=max_tokens)
        if response.content:
            yield response.content


class AsyncToolClient:
    """Pre-configured tool gateway client.

    Calls route through the ``IToolGateway`` middleware chain on the runtime side.
    """

    def __init__(
        self,
        tool_gateway_url: str,
        context: RequestContext,
        agent_id: str,
        tokens: TokenManager | None = None,
    ) -> None:
        self._base_url = tool_gateway_url.rstrip("/") + "/"
        self._agent_id = agent_id
        self._tokens = tokens or TokenManager(context, agent_id)

    async def invoke(self, tool_call: ToolCall) -> ToolResult:
        """Invokes a single tool call and returns the result."""
        body = {
            "toolName": tool_call.name,
            "toolCallId": tool_call.id,
            "arguments": tool_call.arguments,
        }
        resp = await _send(self._base_url, "invoke", body, self._tokens)
        if resp.status_code >= 400:
            raise ToolError(
                f"Tool gateway returned HTTP {resp.status_code}: {resp.text[:500]}"
            )
        data = resp.json()
        return ToolResult(
            tool_call_id=data["toolCallId"],
            content=data["content"],
            is_error=data.get("isError", False),
        )

    async def invoke_all(self, tool_calls: list[ToolCall]) -> list[Message]:
        """Invokes all tool calls concurrently and returns ``role: tool`` messages."""
        results = await asyncio.gather(*[self.invoke(tc) for tc in tool_calls])
        return [
            Message(role="tool", content=r.content, tool_call_id=r.tool_call_id)
            for r in results
        ]


def _serialise_llm_request(
    messages: list[Message],
    model_id: str | None,
    temperature: float | None,
    max_tokens: int | None,
) -> dict[str, Any]:
    """Converts messages to the gateway wire format (camelCase JSON)."""
    return {
        "messages": [_serialise_message(m) for m in messages],
        "modelId": model_id,
        "options": {
            k: v for k, v in {"temperature": temperature, "maxTokens": max_tokens}.items() if v is not None
        } or None,
    }


def _serialise_message(m: Message) -> dict[str, Any]:
    d: dict[str, Any] = {"role": m.role, "content": m.content}
    if m.tool_calls:
        d["toolCalls"] = [{"id": tc.id, "name": tc.name, "arguments": tc.arguments} for tc in m.tool_calls]
    if m.tool_call_id is not None:
        d["toolCallId"] = m.tool_call_id
    return d
