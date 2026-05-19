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
