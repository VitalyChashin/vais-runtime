"""
Canonical wire types for the Vais extension handler protocol (v0.30).
Mirrors contracts/extensions/handler-protocol.md.
"""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any


@dataclass
class AgentInputContext:
    agent_id: str
    run_id: str | None = None
    node_id: str | None = None
    message: str = ""


@dataclass
class AgentOutputContext:
    agent_id: str
    run_id: str
    session_id: str | None = None
    output_tokens: int | None = None
    input_tokens: int | None = None


@dataclass
class PreResponse:
    action: str = "next"                        # "next" | "mutate" | "shortCircuit"
    continuation_token: str | None = None
    context_patch: dict[str, Any] | None = None


@dataclass
class PostResponse:
    action: str = "passThrough"                 # "passThrough" | "mutate"
    context_patch: dict[str, Any] | None = None


# ── toolGatewayMiddleware seam ────────────────────────────────────────────────

@dataclass
class ToolGatewayContext:
    tool_name: str
    call_id: str
    arguments: Any = None                       # parsed tool arguments (typically a dict)
    agent_id: str = ""
    run_id: str | None = None
    privilege_level: str | None = None
    workspace_id: str | None = None
    allowed_tools: list[str] | None = None


@dataclass
class ToolOutcome:
    """The result of dispatching the tool, handed to post() to observe or transform."""
    result: str | None = None
    error: str | None = None


@dataclass
class ToolGatewayPreResponse:
    action: str = "next"                        # "next" | "shortCircuit"
    continuation_token: str | None = None
    result: str | None = None                   # shortCircuit: result returned to the agent
    error: str | None = None                    # shortCircuit: deny/error string


@dataclass
class ToolGatewayPostResponse:
    action: str = "next"                        # "next" | "mutate"
    result: str | None = None                   # mutate: replacement result
    error: str | None = None                    # mutate: replacement error


# ── llmGatewayMiddleware seam ─────────────────────────────────────────────────
#
# Container projection limitations (the in-process C# seam has none): tools are read-only
# (they cannot round-trip back into the request — preserved from the original on mutate);
# streaming is not delivered to container handlers; agent_id/run_id arrive empty.

@dataclass
class LlmToolCall:
    id: str
    name: str
    arguments: Any = None


@dataclass
class LlmMessage:
    role: str
    content: str | None = None
    tool_calls: list[LlmToolCall] | None = None
    tool_call_id: str | None = None


@dataclass
class LlmToolDecl:
    name: str
    description: str | None = None
    parameters_schema: Any = None


@dataclass
class LlmResponseFormat:
    schema: Any = None
    name: str | None = None
    strict: bool = True


@dataclass
class LlmContext:
    """The completion request the handler sees. To rewrite it, mutate this and return
    LlmGatewayPreResponse(action='mutate', request=ctx). Tools are read-only."""
    messages: list[LlmMessage] = field(default_factory=list)
    system_prompt: str | None = None
    temperature: float | None = None
    max_tokens: int | None = None
    tools: list[LlmToolDecl] | None = None
    response_format: LlmResponseFormat | None = None
    agent_id: str = ""
    run_id: str | None = None


@dataclass
class LlmResponse:
    text: str = ""
    prompt_tokens: int | None = None
    completion_tokens: int | None = None


@dataclass
class LlmGatewayPreResponse:
    action: str = "next"                        # "next" | "shortCircuit" | "mutate"
    continuation_token: str | None = None
    response: LlmResponse | None = None         # shortCircuit: synthetic response (skip the model)
    request: LlmContext | None = None           # mutate: replacement request (tools ignored)


@dataclass
class LlmGatewayPostResponse:
    action: str = "next"                        # "next" | "mutate"
    response: LlmResponse | None = None         # mutate: replacement response


# ── errorInterceptor seam ─────────────────────────────────────────────────────
# A single-call hook fired when a turn/node fails. Observe + optionally rewrite the
# user-facing message. It can never suppress the failure or change error_type (P9).

@dataclass
class ErrorContext:
    agent_id: str = ""
    run_id: str | None = None
    node_id: str | None = None
    error_type: str = ""                        # immutable from the handler's perspective
    error_message: str = ""


@dataclass
class ErrorOutcome:
    message: str | None = None                  # non-empty replaces the surfaced message; None = observe-only


@dataclass
class AdvertisedHandler:
    id: str
    seam: str
    pre_endpoint: str
    post_endpoint: str


@dataclass
class HandlerAdvertisement:
    extension_id: str
    version: str
    target_api_version: str
    handlers: list[AdvertisedHandler] = field(default_factory=list)
