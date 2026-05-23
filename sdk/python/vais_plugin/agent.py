"""PluginAgent base class and HTTP server wiring."""

from __future__ import annotations

import asyncio
import dataclasses
import json
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any, AsyncIterator

import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse

from .gateway import AsyncLlmClient, AsyncToolClient
from .models import (
    InvokeRequest,
    InvokeResponse,
    LlmGatewayError,
    Message,
    OpaqueStateDeserializationError,
    RequestContext,
    Timeout,
    ToolCall,
    ToolError,
    UsageCounts,
    JournalEntry,
)

_HEARTBEAT_INTERVAL = 15.0  # seconds


@dataclass
class DeltaPayload:
    """Payload for a *delta* SSE event (incremental text token)."""

    text: str


@dataclass
class ToolStartedPayload:
    """Payload for a *tool.started* SSE event."""

    tool_name: str
    tool_call_id: str


@dataclass
class ToolCompletedPayload:
    """Payload for a *tool.completed* SSE event."""

    tool_name: str
    tool_call_id: str
    output_json: str


@dataclass
class SseEvent:
    """A single SSE event emitted from :meth:`PluginAgent.stream`."""

    event: str
    data: Any  # serialised to JSON on emit

    def encode(self) -> bytes:
        if dataclasses.is_dataclass(self.data):
            payload = dataclasses.asdict(self.data)
        else:
            payload = self.data
        return f"event: {self.event}\ndata: {json.dumps(payload, default=str)}\n\n".encode()


def vais_plugin(target_api_version: str):
    """Class decorator that records plugin metadata for ``GET /v1/metadata``.

    Example::

        @vais_plugin("0.24")
        class AnalystAgent(PluginAgent):
            async def invoke(self, request: InvokeRequest) -> InvokeResponse: ...
    """

    def decorator(cls: type) -> type:
        cls.__vais_plugin__ = {
            "handlerTypeName": f"{cls.__module__}.{cls.__qualname__}",
            "targetApiVersion": target_api_version,
        }
        return cls

    return decorator


class PluginAgent(ABC):
    """Base class for container plugin agents.

    Subclasses must implement :meth:`invoke`.  Override :meth:`stream` for
    incremental token delivery; the default implementation calls :meth:`invoke`
    and emits a single ``done`` event.
    """

    @abstractmethod
    async def invoke(self, request: InvokeRequest) -> InvokeResponse:
        """Handles a plugin invocation.  Use ``request.llm`` and ``request.tools``
        for all LLM and tool calls — direct calls violate architectural principle P4."""

    async def stream(self, request: InvokeRequest) -> AsyncIterator[SseEvent]:
        """Handles a streaming invocation.  Default: calls :meth:`invoke` and yields one *done* event."""
        response = await self.invoke(request)
        yield SseEvent(event="done", data=response)

    def serve(self, port: int | None = None) -> None:
        """Start uvicorn on ``$VAIS_PLUGIN_PORT`` (default 8080)."""
        actual_port = port or int(os.environ.get("VAIS_PLUGIN_PORT", "8080"))
        uvicorn.run(self._build_app(), host="0.0.0.0", port=actual_port)

    def _build_app(self) -> FastAPI:
        app = FastAPI()
        agent = self

        @app.get("/health")
        async def health() -> dict[str, str]:
            return {"status": "ready"}

        @app.get("/v1/metadata")
        async def metadata() -> dict[str, Any]:
            meta: dict[str, Any] = getattr(type(agent), "__vais_plugin__", {})
            return {
                "handlerTypeName": meta.get("handlerTypeName", f"{type(agent).__module__}.{type(agent).__qualname__}"),
                "targetApiVersion": meta.get("targetApiVersion", "0.24"),
                "capabilities": ["invoke", "stream"],
                "sdkVersion": "0.1.0",
            }

        @app.post("/v1/invoke")
        async def invoke_endpoint(raw: Request) -> JSONResponse:
            body = await raw.json()
            request = _parse_request(body)
            request.llm = AsyncLlmClient(request.llm_gateway_url, request.context, request.agent_id)
            request.tools = AsyncToolClient(request.tool_gateway_url, request.context, request.agent_id)
            try:
                async with asyncio.timeout(request.timeout_seconds):
                    response = await agent.invoke(request)
                return JSONResponse(content=_serialise_response(response))
            except OpaqueStateDeserializationError as exc:
                return _error_json(422, "OpaqueStateDeserializationError", exc, with_trace=False)
            except LlmGatewayError as exc:
                return _error_json(502, "LlmGatewayError", exc)
            except ToolError as exc:
                return _error_json(503, "ToolError", exc)
            except (Timeout, asyncio.TimeoutError) as exc:
                return _error_json(504, "Timeout", exc)
            except Exception as exc:
                return _error_json(500, "InternalError", exc)

        @app.post("/v1/stream")
        async def stream_endpoint(raw: Request) -> StreamingResponse:
            body = await raw.json()
            request = _parse_request(body)
            request.llm = AsyncLlmClient(request.llm_gateway_url, request.context, request.agent_id)
            request.tools = AsyncToolClient(request.tool_gateway_url, request.context, request.agent_id)

            async def generate() -> AsyncIterator[bytes]:
                error_type: str | None = None
                error: Exception | None = None
                try:
                    async with asyncio.timeout(request.timeout_seconds):
                        async for event in agent.stream(request):
                            yield event.encode()
                except OpaqueStateDeserializationError as exc:
                    error_type, error = "OpaqueStateDeserializationError", exc
                except LlmGatewayError as exc:
                    error_type, error = "LlmGatewayError", exc
                except ToolError as exc:
                    error_type, error = "ToolError", exc
                except (Timeout, asyncio.TimeoutError) as exc:
                    error_type, error = "Timeout", exc
                except Exception as exc:
                    error_type, error = "InternalError", exc
                if error is not None:
                    yield _error_event(error_type, str(error))
                    yield SseEvent("done", InvokeResponse(assistant_message="")).encode()

            return StreamingResponse(
                _heartbeat_wrapper(generate()),
                media_type="text/event-stream",
                headers={"Cache-Control": "no-cache"},
            )

        return app


async def _heartbeat_wrapper(source: AsyncIterator[bytes]) -> AsyncIterator[bytes]:
    """Wraps an event source with 15-second heartbeat comments when the source is slow."""
    heartbeat = b": heartbeat\n\n"
    queue: asyncio.Queue[bytes | None] = asyncio.Queue()

    async def producer() -> None:
        async for chunk in source:
            await queue.put(chunk)
        await queue.put(None)  # sentinel

    task = asyncio.create_task(producer())
    try:
        while True:
            try:
                item = await asyncio.wait_for(queue.get(), timeout=_HEARTBEAT_INTERVAL)
            except asyncio.TimeoutError:
                yield heartbeat
                continue
            if item is None:
                break
            yield item
    finally:
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass


def _error_event(error_type: str, error_message: str) -> bytes:
    payload = json.dumps({"errorType": error_type, "errorMessage": error_message})
    return f"event: error\ndata: {payload}\n\n".encode()


def _error_json(status_code: int, error_type: str, exc: Exception, *, with_trace: bool = True) -> JSONResponse:
    """Builds an error JSONResponse with the contract error body. ``with_trace`` attaches the current
    traceback (truncated to 500 chars) as ``diagnosticTail``; off for 422 (no trace is useful there)."""
    diag: str | None = None
    if with_trace:
        import traceback
        diag = traceback.format_exc()[:500]
    return JSONResponse(
        status_code=status_code,
        content={"errorType": error_type, "errorMessage": str(exc), "diagnosticTail": diag},
    )


def _parse_request(body: dict[str, Any]) -> InvokeRequest:
    ctx_raw = body.get("context") or {}
    context = RequestContext(
        traceparent=ctx_raw.get("traceparent"),
        run_id=ctx_raw.get("runId"),
        correlation_id=ctx_raw.get("correlationId"),
        call_token=ctx_raw.get("callToken", ""),
    )
    messages = [
        Message(
            role=m["role"],
            content=m.get("content"),
            tool_calls=[
                ToolCall(id=tc["id"], name=tc["name"], arguments=tc.get("arguments", {}))
                for tc in (m.get("toolCalls") or [])
            ],
            tool_call_id=m.get("toolCallId"),
        )
        for m in (body.get("messages") or [])
    ]
    return InvokeRequest(
        agent_id=body.get("agentId", ""),
        session_id=body.get("sessionId", ""),
        messages=messages,
        llm_gateway_url=body.get("llmGatewayUrl", ""),
        tool_gateway_url=body.get("toolGatewayUrl", ""),
        opaque_state=body.get("opaqueState"),
        timeout_seconds=body.get("timeoutSeconds", 60),
        context=context,
    )


def _serialise_response(r: InvokeResponse) -> dict[str, Any]:
    d: dict[str, Any] = {"assistantMessage": r.assistant_message}
    if r.opaque_state is not None:
        d["opaqueState"] = r.opaque_state
    if r.journal:
        d["journal"] = [
            {
                "toolName": j.tool_name,
                "toolCallId": j.tool_call_id,
                "inputJson": j.input_json,
                "outputJson": j.output_json,
            }
            for j in r.journal
        ]
    if r.usage is not None:
        d["usage"] = {
            "inputTokens": r.usage.input_tokens,
            "outputTokens": r.usage.output_tokens,
            "cachedTokens": r.usage.cached_tokens,
        }
    return d
