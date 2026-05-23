"""Wire types for the container plugin protocol (IP-1 v0.24)."""

from __future__ import annotations

import dataclasses
from dataclasses import dataclass, field
from typing import Any, TypeVar

_T = TypeVar("_T")


@dataclass
class ToolCall:
    """A single tool call requested by the model."""

    id: str
    name: str
    arguments: dict[str, Any]


@dataclass
class Message:
    """A single conversation turn in the IP-1 wire format.

    Field mapping to/from camelCase JSON:
      tool_calls  <-> toolCalls
      tool_call_id <-> toolCallId
    """

    role: str  # "system" | "user" | "assistant" | "tool"
    content: str | None = None
    tool_calls: list[ToolCall] = field(default_factory=list)
    tool_call_id: str | None = None


@dataclass
class RequestContext:
    """Ambient context for a plugin invocation."""

    traceparent: str | None = None
    run_id: str | None = None
    correlation_id: str | None = None
    call_token: str = ""
    # Session mode only: URL the SDK POSTs to (current token as Bearer) for a fresh token.
    # None for short-turn plugins, which use the single call_token and never renew.
    renew_url: str | None = None


@dataclass
class JournalEntry:
    """Record of one tool call within a single invocation."""

    tool_name: str
    tool_call_id: str
    input_json: str
    output_json: str


@dataclass
class UsageCounts:
    """Token usage counts for an LLM call."""

    input_tokens: int = 0
    output_tokens: int = 0
    cached_tokens: int = 0


class OpaqueStateDeserializationError(Exception):
    """Raised by :func:`InvokeRequest.opaque_state_as` when the stored JSON
    cannot be deserialised into the requested type.  The SDK intercepts this
    exception and returns HTTP 422, which causes the runtime grain to clear
    its stored state and retry with ``opaqueState: null``.
    """


class PluginError(Exception):
    """Base for plugin errors the SDK maps to a distinct HTTP status code.

    The ``/v1/invoke`` and ``/v1/stream`` handlers catch each subclass and emit the matching error
    response. Plugin authors may raise them directly; the gateway clients also raise
    :class:`LlmGatewayError` and :class:`ToolError` automatically.
    """


class LlmGatewayError(PluginError):
    """LLM gateway middleware chain failed (HTTP 502). Auto-raised by ``AsyncLlmClient`` on an upstream
    non-2xx; may also be raised manually."""


class ToolError(PluginError):
    """Tool-layer failure surfaced to the runtime (HTTP 503). Auto-raised by ``AsyncToolClient`` when a
    tool call cannot be dispatched (non-2xx from the tool gateway). Raise manually to give up on a tool
    that returned an error result."""


class Timeout(PluginError):
    """Invocation exceeded its ``timeoutSeconds`` budget (HTTP 504). Auto-raised by the SDK when the
    budget elapses; may also be raised manually."""


@dataclass
class InvokeRequest:
    """Request sent by the runtime shim to ``POST /v1/invoke`` and ``POST /v1/stream``."""

    agent_id: str
    session_id: str
    messages: list[Message]
    llm_gateway_url: str
    tool_gateway_url: str
    opaque_state: dict[str, Any] | None
    timeout_seconds: int
    context: RequestContext

    # Set by PluginAgent before dispatching to invoke(); not in the wire format.
    llm: Any = field(init=False, repr=False, default=None)
    tools: Any = field(init=False, repr=False, default=None)

    def opaque_state_as(self, cls: type[_T]) -> _T | None:
        """Deserialise :attr:`opaque_state` into *cls*.

        Returns ``None`` when ``opaque_state`` is ``None`` (first call or fresh-start).
        Raises :class:`OpaqueStateDeserializationError` on schema mismatch.
        """
        if self.opaque_state is None:
            return None
        try:
            return cls(**self.opaque_state)  # type: ignore[call-arg]
        except Exception as exc:
            raise OpaqueStateDeserializationError(str(exc)) from exc


@dataclass
class InvokeResponse:
    """Response returned by ``POST /v1/invoke`` and carried as the terminal *done* SSE event."""

    assistant_message: str
    opaque_state: dict[str, Any] | None = None
    journal: list[JournalEntry] = field(default_factory=list)
    usage: UsageCounts | None = None
