"""
FastAPI host that auto-routes /handlers/<id>/pre and /handlers/<id>/post
for every registered handler. Implements GET /v1/handlers for discovery.
"""
from __future__ import annotations
import uuid
from dataclasses import dataclass
from typing import Any, Callable
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel
from .middleware import AgentInputMiddleware, AgentOutputMiddleware, ToolGatewayMiddleware
from .wire import (
    AgentInputContext, AgentOutputContext,
    PreResponse, PostResponse,
    ToolGatewayContext, ToolOutcome,
    AdvertisedHandler, HandlerAdvertisement,
)

# ── Pydantic request/response models ──────────────────────────────────────────

class _InputContextBody(BaseModel):
    model_config = ConfigDict(populate_by_name=True)
    agent_id: str
    run_id: str | None = None
    node_id: str | None = None
    message: str = ""


class _OutputContextBody(BaseModel):
    model_config = ConfigDict(populate_by_name=True)
    agent_id: str
    run_id: str
    session_id: str | None = None
    output_tokens: int | None = None
    input_tokens: int | None = None


# The handler protocol envelope is camelCase on the wire (matches the C# runtime proxy,
# the inner context object, and every other vais manifest/wire shape). populate_by_name
# keeps snake_case construction working in Python; FastAPI serializes responses by alias.
class _CamelModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class _PreRequestBody(_CamelModel):
    call_id: str
    context: dict[str, Any]


class _PostRequestBody(_CamelModel):
    call_id: str
    continuation_token: str | None = None


class _PreResponseBody(_CamelModel):
    action: str
    continuation_token: str | None = None
    context_patch: dict[str, Any] | None = None


class _PostResponseBody(_CamelModel):
    action: str
    context_patch: dict[str, Any] | None = None


class _ToolPreResponseBody(_CamelModel):
    action: str
    continuation_token: str | None = None
    result: str | None = None
    error: str | None = None


class _ToolPostRequestBody(_CamelModel):
    call_id: str
    continuation_token: str | None = None
    outcome_result: str | None = None
    outcome_error: str | None = None


class _ToolPostResponseBody(_CamelModel):
    action: str
    result: str | None = None
    error: str | None = None


# ── Context builders ──────────────────────────────────────────────────────────

def _build_input_context(c: dict[str, Any]) -> AgentInputContext:
    return AgentInputContext(
        agent_id=c.get("agentId", ""),
        run_id=c.get("runId"),
        node_id=c.get("nodeId"),
        message=c.get("message", ""),
    )


def _build_output_context(c: dict[str, Any]) -> AgentOutputContext:
    return AgentOutputContext(
        agent_id=c.get("agentId", ""),
        run_id=c.get("runId", ""),
        session_id=c.get("sessionId"),
        output_tokens=c.get("outputTokens"),
        input_tokens=c.get("inputTokens"),
    )


def _build_tool_context(c: dict[str, Any]) -> ToolGatewayContext:
    return ToolGatewayContext(
        tool_name=c.get("toolName", ""),
        call_id=c.get("callId", ""),
        arguments=c.get("arguments"),
        agent_id=c.get("agentId", ""),
        run_id=c.get("runId"),
        privilege_level=c.get("privilegeLevel"),
        workspace_id=c.get("workspaceId"),
        allowed_tools=c.get("allowedTools"),
    )


# ── Route registrars ──────────────────────────────────────────────────────────
# Handler/build_context are captured by closure (these run once per handler), never as FastAPI
# endpoint default args — a defaulted parameter would be treated as a request field and a fresh
# handler instantiated per call, losing handler state.

def _uniform_registrar(
    build_context: Callable[[dict[str, Any]], Any],
) -> Callable[[FastAPI, str, Any], None]:
    """agentInput/agentOutput share a contextPatch-based pre/post protocol."""
    def register(app: FastAPI, handler_id: str, handler: Any) -> None:
        @app.post(f"/handlers/{handler_id}/pre", name=f"{handler_id}_pre")
        async def handler_pre(body: _PreRequestBody) -> _PreResponseBody:
            resp = await handler.pre(build_context(body.context), body.call_id)
            return _PreResponseBody(
                action=resp.action,
                continuation_token=resp.continuation_token,
                context_patch=resp.context_patch,
            )

        @app.post(f"/handlers/{handler_id}/post", name=f"{handler_id}_post")
        async def handler_post(body: _PostRequestBody) -> _PostResponseBody:
            resp = await handler.post(body.call_id, body.continuation_token)
            return _PostResponseBody(action=resp.action, context_patch=resp.context_patch)

    return register


def _register_tool_routes(app: FastAPI, handler_id: str, handler: Any) -> None:
    """toolGatewayMiddleware: pre may deny/short-circuit; post carries + may transform the outcome."""
    @app.post(f"/handlers/{handler_id}/pre", name=f"{handler_id}_pre")
    async def tool_pre(body: _PreRequestBody) -> _ToolPreResponseBody:
        resp = await handler.pre(_build_tool_context(body.context), body.call_id)
        return _ToolPreResponseBody(
            action=resp.action,
            continuation_token=resp.continuation_token,
            result=resp.result,
            error=resp.error,
        )

    @app.post(f"/handlers/{handler_id}/post", name=f"{handler_id}_post")
    async def tool_post(body: _ToolPostRequestBody) -> _ToolPostResponseBody:
        outcome = ToolOutcome(result=body.outcome_result, error=body.outcome_error)
        resp = await handler.post(body.call_id, body.continuation_token, outcome)
        return _ToolPostResponseBody(action=resp.action, result=resp.result, error=resp.error)


# ── Seam registry ───────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class _SeamSpec:
    """One row per seam: its wire name, the ABC it matches, and how it registers pre/post routes."""
    name: str
    abc: type
    register_routes: Callable[[FastAPI, str, Any], None]


_SEAMS: tuple[_SeamSpec, ...] = (
    _SeamSpec("agentInput", AgentInputMiddleware, _uniform_registrar(_build_input_context)),
    _SeamSpec("agentOutput", AgentOutputMiddleware, _uniform_registrar(_build_output_context)),
    _SeamSpec("toolGatewayMiddleware", ToolGatewayMiddleware, _register_tool_routes),
)


def _spec_for(handler: Any) -> _SeamSpec | None:
    for spec in _SEAMS:
        if isinstance(handler, spec.abc):
            return spec
    return None


# ── Host ──────────────────────────────────────────────────────────────────────

class Host:
    """
    FastAPI-backed host for a container extension.
    Registers /v1/handlers (discovery) and /handlers/<id>/pre|post for each handler.

    Usage::

        app = Host(
            extension_id="vais-ext-log",
            version="1.0.0",
            target_api_version="0.30",
            handlers={"in": LogInput(), "out": LogOutput()},
        ).fastapi
    """

    def __init__(
        self,
        extension_id: str,
        version: str,
        handlers: dict[str, AgentInputMiddleware | AgentOutputMiddleware | ToolGatewayMiddleware],
        target_api_version: str = "0.30",
    ) -> None:
        self._extension_id = extension_id
        self._version = version
        self._target_api_version = target_api_version
        self._handlers: dict[str, AgentInputMiddleware | AgentOutputMiddleware | ToolGatewayMiddleware] = handlers
        self._app = self._build_app()

    @property
    def fastapi(self) -> FastAPI:
        return self._app

    def _build_app(self) -> FastAPI:
        app = FastAPI(title=f"vais-extension:{self._extension_id}")

        advertised: list[AdvertisedHandler] = []
        for handler_id, handler in self._handlers.items():
            advertised.append(AdvertisedHandler(
                id=handler_id,
                seam=self._seam_name(handler),
                pre_endpoint=f"/handlers/{handler_id}/pre",
                post_endpoint=f"/handlers/{handler_id}/post",
            ))
            self._register_handler_routes(app, handler_id, handler)

        adv = HandlerAdvertisement(
            extension_id=self._extension_id,
            version=self._version,
            target_api_version=self._target_api_version,
            handlers=advertised,
        )

        @app.get("/v1/handlers")
        async def get_handlers():
            return {
                "extensionId": adv.extension_id,
                "version": adv.version,
                "targetApiVersion": adv.target_api_version,
                "handlers": [
                    {
                        "id": h.id,
                        "seam": h.seam,
                        "preEndpoint": h.pre_endpoint,
                        "postEndpoint": h.post_endpoint,
                    }
                    for h in adv.handlers
                ],
            }

        return app

    def _register_handler_routes(self, app: FastAPI, handler_id: str, handler: Any) -> None:
        spec = _spec_for(handler)
        if spec is None:
            # Unknown handler type: advertised as "unknown" with no routes (matches prior behavior).
            return
        spec.register_routes(app, handler_id, handler)

    @staticmethod
    def _seam_name(handler: Any) -> str:
        spec = _spec_for(handler)
        return spec.name if spec is not None else "unknown"
