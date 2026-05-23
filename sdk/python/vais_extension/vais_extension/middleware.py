"""
Abstract base classes for Vais extension handler seams.
Each seam corresponds to a pipeline hook in the Vais.Agents runtime.
"""
from __future__ import annotations
from abc import ABC, abstractmethod
from typing import Awaitable, Callable
from typing import Any
from .wire import (
    AgentInputContext, AgentOutputContext, PreResponse, PostResponse,
    ToolGatewayContext, ToolOutcome, ToolGatewayPreResponse, ToolGatewayPostResponse,
    LlmContext, LlmResponse, LlmGatewayPreResponse, LlmGatewayPostResponse,
    ErrorContext, ErrorOutcome,
    GraphNodeContext, GraphNodePreResponse, GraphNodePostResponse,
)


class AgentInputMiddleware(ABC):
    """
    Abstract base for agentInput seam handlers.
    Fires once per agent invocation before the LLM call.
    """

    @abstractmethod
    async def pre(
        self,
        context: AgentInputContext,
        call_id: str,
    ) -> PreResponse:
        """
        Called before the agent processes the input message.
        Return PreResponse(action="shortCircuit") to suppress the LLM call.
        Return PreResponse(action="mutate", context_patch={...}) to modify context properties.
        """
        ...

    async def post(
        self,
        call_id: str,
        continuation_token: str | None,
    ) -> PostResponse:
        """
        Called after the wrapped operation completes (only when pre returned next/mutate).
        """
        return PostResponse()


class AgentOutputMiddleware(ABC):
    """
    Abstract base for agentOutput seam handlers.
    Fires once per LLM call (per round-trip in tool-calling loops).
    """

    @abstractmethod
    async def pre(
        self,
        context: AgentOutputContext,
        call_id: str,
    ) -> PreResponse:
        """Called after the LLM returns a response."""
        ...

    async def post(
        self,
        call_id: str,
        continuation_token: str | None,
    ) -> PostResponse:
        """Called after the wrapped operation completes."""
        return PostResponse()


class ToolGatewayMiddleware(ABC):
    """
    Abstract base for toolGatewayMiddleware seam handlers.
    Fires once per tool call. Capability matches the in-process gateway: observe,
    short-circuit (deny / cached result), or transform the outcome. Arguments cannot be
    rewritten into the tool (the runtime dispatches the original request).
    """

    @abstractmethod
    async def pre(
        self,
        context: ToolGatewayContext,
        call_id: str,
    ) -> ToolGatewayPreResponse:
        """
        Called before the tool is dispatched.
        Return ToolGatewayPreResponse(action="shortCircuit", error=...) to deny without dispatch,
        or (action="shortCircuit", result=...) to return a cached/substitute result.
        Return ToolGatewayPreResponse(action="next") to dispatch the tool.
        """
        ...

    async def post(
        self,
        call_id: str,
        continuation_token: str | None,
        outcome: ToolOutcome,
    ) -> ToolGatewayPostResponse:
        """
        Called after the tool is dispatched (only when pre returned next). Observe or transform
        the outcome; return ToolGatewayPostResponse(action="mutate", result=...) to replace it.
        """
        return ToolGatewayPostResponse()


class LlmGatewayMiddleware(ABC):
    """
    Abstract base for llmGatewayMiddleware seam handlers (non-streaming path).
    Fires once per LLM call. Capability: observe, short-circuit (synthetic response),
    transform the response, or rewrite the request (messages/params only — tools are read-only).
    Streaming calls bypass container handlers.
    """

    @abstractmethod
    async def pre(
        self,
        context: LlmContext,
        call_id: str,
    ) -> LlmGatewayPreResponse:
        """
        Called before the model is invoked.
        Return LlmGatewayPreResponse(action="shortCircuit", response=LlmResponse(...)) to skip the model,
        (action="mutate", request=context) to rewrite the request, or (action="next") to proceed.
        """
        ...

    async def post(
        self,
        call_id: str,
        continuation_token: str | None,
        response: LlmResponse,
    ) -> LlmGatewayPostResponse:
        """
        Called after the model returns (only when pre returned next). Observe or transform
        the response; return LlmGatewayPostResponse(action="mutate", response=...) to replace it.
        """
        return LlmGatewayPostResponse()


class ErrorInterceptor(ABC):
    """
    Abstract base for errorInterceptor seam handlers. A single-call hook fired when an agent turn
    or graph node fails. Observe (audit/alert) and optionally rewrite the user-facing message;
    it can never suppress the failure or change error_type (P9).
    """

    @abstractmethod
    async def on_error(self, context: ErrorContext, call_id: str) -> ErrorOutcome:
        """
        Return ErrorOutcome(message="...") to replace the surfaced error message, or
        ErrorOutcome() (default) to observe only.
        """
        ...


class GraphNodeMiddleware(ABC):
    """
    Abstract base for graphNode seam handlers. Wraps a graph node's body execution.
    Capability: observe, short-circuit (substitute output without running the node — caching/deny),
    or transform the node output. Short-circuit is journaling-safe (the runtime merges + checkpoints
    the substitute output like a real run). Hot seam — adds a per-node round-trip.
    """

    @abstractmethod
    async def pre(self, context: GraphNodeContext, call_id: str) -> GraphNodePreResponse:
        """
        Called before the node body runs.
        Return GraphNodePreResponse(action="shortCircuit", output={...}) to substitute the node's
        output without running it, or GraphNodePreResponse(action="next") to run the body.
        """
        ...

    async def post(
        self,
        call_id: str,
        continuation_token: str | None,
        output: dict[str, Any],
    ) -> GraphNodePostResponse:
        """
        Called after the node body runs (only when pre returned next). Observe or transform the
        output; return GraphNodePostResponse(action="mutate", output={...}) to replace it.
        """
        return GraphNodePostResponse()
