"""
FastAPI host that auto-routes /handlers/<id>/pre and /handlers/<id>/post
for every registered handler. Implements GET /v1/handlers for discovery.
"""
from __future__ import annotations
import uuid
from typing import Any
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, ConfigDict
from .middleware import AgentInputMiddleware, AgentOutputMiddleware
from .wire import (
    AgentInputContext, AgentOutputContext,
    PreResponse, PostResponse,
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


class _PreRequestBody(BaseModel):
    call_id: str
    context: dict[str, Any]


class _PostRequestBody(BaseModel):
    call_id: str
    continuation_token: str | None = None


class _PreResponseBody(BaseModel):
    action: str
    continuation_token: str | None = None
    context_patch: dict[str, Any] | None = None


class _PostResponseBody(BaseModel):
    action: str
    context_patch: dict[str, Any] | None = None


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
        handlers: dict[str, AgentInputMiddleware | AgentOutputMiddleware],
        target_api_version: str = "0.30",
    ) -> None:
        self._extension_id = extension_id
        self._version = version
        self._target_api_version = target_api_version
        self._handlers: dict[str, AgentInputMiddleware | AgentOutputMiddleware] = handlers
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

    def _register_handler_routes(
        self,
        app: FastAPI,
        handler_id: str,
        handler: AgentInputMiddleware | AgentOutputMiddleware,
    ) -> None:
        pre_path = f"/handlers/{handler_id}/pre"
        post_path = f"/handlers/{handler_id}/post"

        if isinstance(handler, AgentInputMiddleware):
            input_handler = handler

            @app.post(pre_path)
            async def input_pre(body: _PreRequestBody, _h=input_handler) -> _PreResponseBody:
                ctx_data = body.context
                ctx = AgentInputContext(
                    agent_id=ctx_data.get("agentId", ""),
                    run_id=ctx_data.get("runId"),
                    node_id=ctx_data.get("nodeId"),
                    message=ctx_data.get("message", ""),
                )
                resp = await _h.pre(ctx, body.call_id)
                return _PreResponseBody(
                    action=resp.action,
                    continuation_token=resp.continuation_token,
                    context_patch=resp.context_patch,
                )

            @app.post(post_path)
            async def input_post(body: _PostRequestBody, _h=input_handler) -> _PostResponseBody:
                resp = await _h.post(body.call_id, body.continuation_token)
                return _PostResponseBody(action=resp.action, context_patch=resp.context_patch)

        elif isinstance(handler, AgentOutputMiddleware):
            output_handler = handler

            @app.post(pre_path)
            async def output_pre(body: _PreRequestBody, _h=output_handler) -> _PreResponseBody:
                ctx_data = body.context
                ctx = AgentOutputContext(
                    agent_id=ctx_data.get("agentId", ""),
                    run_id=ctx_data.get("runId", ""),
                    session_id=ctx_data.get("sessionId"),
                    output_tokens=ctx_data.get("outputTokens"),
                    input_tokens=ctx_data.get("inputTokens"),
                )
                resp = await _h.pre(ctx, body.call_id)
                return _PreResponseBody(
                    action=resp.action,
                    continuation_token=resp.continuation_token,
                    context_patch=resp.context_patch,
                )

            @app.post(post_path)
            async def output_post(body: _PostRequestBody, _h=output_handler) -> _PostResponseBody:
                resp = await _h.post(body.call_id, body.continuation_token)
                return _PostResponseBody(action=resp.action, context_patch=resp.context_patch)

    @staticmethod
    def _seam_name(handler: AgentInputMiddleware | AgentOutputMiddleware) -> str:
        if isinstance(handler, AgentInputMiddleware):
            return "agentInput"
        if isinstance(handler, AgentOutputMiddleware):
            return "agentOutput"
        return "unknown"
