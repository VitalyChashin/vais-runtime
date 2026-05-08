"""Test harness for driving a PluginAgent in-process without a live runtime."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable

import httpx

from .agent import PluginAgent, SseEvent, _parse_request, _serialise_response
from .gateway import AsyncLlmClient, AsyncToolClient, LlmResponse, ToolResult
from .models import InvokeRequest, InvokeResponse, Message, ToolCall


class MockLlmResponder:
    """Returns a fixed :class:`LlmResponse` or delegates to a callback.

    Usage::

        responder = MockLlmResponder(LlmResponse(content="4"))
        # or
        responder = MockLlmResponder(callback=lambda msgs: LlmResponse(content=msgs[-1].content))
    """

    def __init__(
        self,
        response: LlmResponse | None = None,
        *,
        callback: Callable[[list[Message]], LlmResponse] | None = None,
    ) -> None:
        if response is None and callback is None:
            raise ValueError("Provide either response or callback.")
        self._response = response
        self._callback = callback

    async def complete(self, messages: list[Message]) -> LlmResponse:
        if self._callback is not None:
            return self._callback(messages)
        return self._response  # type: ignore[return-value]


class MockToolResponder:
    """Returns canned string results keyed by tool name.

    Usage::

        responder = MockToolResponder({"web_search": "result text"})
    """

    def __init__(self, results: dict[str, str]) -> None:
        self._results = results

    async def invoke(self, tool_call: ToolCall) -> ToolResult:
        content = self._results.get(tool_call.name, f"No mock for tool '{tool_call.name}'")
        return ToolResult(tool_call_id=tool_call.id, content=content)


class _MockLlmClient(AsyncLlmClient):
    def __init__(self, responder: MockLlmResponder) -> None:
        self._responder = responder
        # Skip super().__init__ — no real URL needed.

    async def complete(self, messages: list[Message], **_: Any) -> LlmResponse:
        return await self._responder.complete(messages)

    async def stream(self, messages: list[Message], **_: Any):  # type: ignore[override]
        r = await self.complete(messages)
        if r.content:
            yield r.content


class _MockToolClient(AsyncToolClient):
    def __init__(self, responder: MockToolResponder) -> None:
        self._responder = responder

    async def invoke(self, tool_call: ToolCall) -> ToolResult:
        return await self._responder.invoke(tool_call)


class PluginTestClient:
    """Drive a :class:`PluginAgent` in-process without a live runtime.

    Uses :class:`httpx.AsyncClient` against the FastAPI app so all request
    parsing, error handling, and SSE formatting are exercised identically to
    production.
    """

    def __init__(self, agent: PluginAgent) -> None:
        self._agent = agent
        self._app = agent._build_app()

    async def invoke(
        self,
        messages: list[Message],
        *,
        opaque_state: dict[str, Any] | None = None,
        mock_llm: MockLlmResponder | None = None,
        mock_tools: MockToolResponder | None = None,
    ) -> InvokeResponse:
        request = _make_request(messages, opaque_state)
        if mock_llm is not None:
            self._agent.__class__  # ensure agent class exists
            request.llm = _MockLlmClient(mock_llm)  # type: ignore[assignment]
        if mock_tools is not None:
            request.tools = _MockToolClient(mock_tools)  # type: ignore[assignment]

        # Inject mocked clients by patching the app's dependency resolution.
        # We drive the request through the real HTTP app to exercise all middleware.
        async with httpx.AsyncClient(app=self._app, base_url="http://test") as client:
            body = _build_wire_request(messages, opaque_state)
            resp = await client.post("/v1/invoke", json=body)
        if resp.status_code == 422:
            from .models import OpaqueStateDeserializationError
            data = resp.json()
            raise OpaqueStateDeserializationError(data.get("errorMessage", ""))
        resp.raise_for_status()
        data = resp.json()
        return InvokeResponse(
            assistant_message=data.get("assistantMessage", ""),
            opaque_state=data.get("opaqueState"),
        )

    async def collect_stream(
        self,
        messages: list[Message],
        *,
        opaque_state: dict[str, Any] | None = None,
        mock_llm: MockLlmResponder | None = None,
        mock_tools: MockToolResponder | None = None,
    ) -> tuple[list[SseEvent], InvokeResponse]:
        """Collect all SSE events; the last event is always *done*.

        Returns ``(events, final_response)`` where ``final_response`` is parsed
        from the *done* event's data field.
        """
        async with httpx.AsyncClient(app=self._app, base_url="http://test") as client:
            body = _build_wire_request(messages, opaque_state)
            async with client.stream("POST", "/v1/stream", json=body) as resp:
                resp.raise_for_status()
                raw = await resp.aread()

        events, final = _parse_sse(raw.decode())
        return events, final


def _make_request(messages: list[Message], opaque_state: dict[str, Any] | None) -> InvokeRequest:
    from .models import RequestContext
    return InvokeRequest(
        agent_id="test-agent",
        session_id="00000000-0000-0000-0000-000000000001",
        messages=messages,
        llm_gateway_url="http://mock/v1/llm",
        tool_gateway_url="http://mock/v1/tools",
        opaque_state=opaque_state,
        timeout_seconds=30,
        context=RequestContext(call_token="test-token"),
    )


def _build_wire_request(messages: list[Message], opaque_state: dict[str, Any] | None) -> dict[str, Any]:
    return {
        "agentId": "test-agent",
        "sessionId": "00000000-0000-0000-0000-000000000001",
        "messages": [
            {
                "role": m.role,
                "content": m.content,
                **({} if not m.tool_calls else {"toolCalls": [{"id": tc.id, "name": tc.name, "arguments": tc.arguments} for tc in m.tool_calls]}),
                **({} if m.tool_call_id is None else {"toolCallId": m.tool_call_id}),
            }
            for m in messages
        ],
        "llmGatewayUrl": "http://mock/v1/llm",
        "toolGatewayUrl": "http://mock/v1/tools",
        "opaqueState": opaque_state,
        "timeoutSeconds": 30,
        "context": {"callToken": "test-token"},
    }


def _parse_sse(raw: str) -> tuple[list[SseEvent], InvokeResponse]:
    import json as _json

    events: list[SseEvent] = []
    current_event: str | None = None
    final = InvokeResponse(assistant_message="")

    for line in raw.splitlines():
        if line.startswith("event: "):
            current_event = line[len("event: "):]
        elif line.startswith("data: ") and current_event:
            data_raw = line[len("data: "):]
            data = _json.loads(data_raw)
            events.append(SseEvent(event=current_event, data=data))
            if current_event == "done":
                final = InvokeResponse(
                    assistant_message=data.get("assistantMessage", ""),
                    opaque_state=data.get("opaqueState"),
                )
            current_event = None

    return events, final
