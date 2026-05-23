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
