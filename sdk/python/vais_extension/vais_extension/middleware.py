"""
Abstract base classes for Vais extension handler seams.
Each seam corresponds to a pipeline hook in the Vais.Agents runtime.
"""
from __future__ import annotations
from abc import ABC, abstractmethod
from typing import Awaitable, Callable
from .wire import (
    AgentInputContext, AgentOutputContext, PreResponse, PostResponse,
    ToolGatewayContext, ToolOutcome, ToolGatewayPreResponse, ToolGatewayPostResponse,
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
