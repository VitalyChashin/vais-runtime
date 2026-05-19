"""
Abstract base classes for Vais extension handler seams.
Each seam corresponds to a pipeline hook in the Vais.Agents runtime.
"""
from __future__ import annotations
from abc import ABC, abstractmethod
from typing import Awaitable, Callable
from .wire import AgentInputContext, AgentOutputContext, PreResponse, PostResponse


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
