"""
vais_extension — Python SDK for Vais.Agents container extensions.

Implement middleware handlers by subclassing AgentInputMiddleware or AgentOutputMiddleware,
then wire them to a FastAPI app via Host.

Example::

    from vais_extension import AgentInputMiddleware, AgentOutputMiddleware, Host
    from vais_extension.wire import AgentInputContext, AgentOutputContext, PreResponse, PostResponse

    class LogInput(AgentInputMiddleware):
        async def pre(self, context: AgentInputContext, call_id: str) -> PreResponse:
            print(f"[ext-log] in {context.agent_id}: {context.message}")
            return PreResponse(action="next")

    class LogOutput(AgentOutputMiddleware):
        async def pre(self, context: AgentOutputContext, call_id: str) -> PreResponse:
            print(f"[ext-log] out {context.agent_id}: {context.output_tokens or 0} tok")
            return PreResponse(action="next")

    app = Host(
        extension_id="vais-ext-log-python",
        version="0.1.0",
        target_api_version="0.30",
        handlers={"in": LogInput(), "out": LogOutput()},
    ).fastapi
"""

from . import _log_handler as _log_handler_module
_log_handler_module._configure_log_handler()

from .middleware import (
    AgentInputMiddleware, AgentOutputMiddleware, ToolGatewayMiddleware, LlmGatewayMiddleware,
    ErrorInterceptor,
)
from .host import Host
from .wire import (
    AgentInputContext,
    AgentOutputContext,
    PreResponse,
    PostResponse,
    ToolGatewayContext,
    ToolOutcome,
    ToolGatewayPreResponse,
    ToolGatewayPostResponse,
    LlmContext,
    LlmResponse,
    LlmMessage,
    LlmToolCall,
    LlmToolDecl,
    LlmResponseFormat,
    LlmGatewayPreResponse,
    LlmGatewayPostResponse,
    ErrorContext,
    ErrorOutcome,
    HandlerAdvertisement,
    AdvertisedHandler,
)
from .telemetry import extract_parent_context, span

__all__ = [
    "AgentInputMiddleware",
    "AgentOutputMiddleware",
    "ToolGatewayMiddleware",
    "LlmGatewayMiddleware",
    "ErrorInterceptor",
    "Host",
    "AgentInputContext",
    "AgentOutputContext",
    "PreResponse",
    "PostResponse",
    "ToolGatewayContext",
    "ToolOutcome",
    "ToolGatewayPreResponse",
    "ToolGatewayPostResponse",
    "LlmContext",
    "LlmResponse",
    "LlmMessage",
    "LlmToolCall",
    "LlmToolDecl",
    "LlmResponseFormat",
    "LlmGatewayPreResponse",
    "LlmGatewayPostResponse",
    "ErrorContext",
    "ErrorOutcome",
    "HandlerAdvertisement",
    "AdvertisedHandler",
    "extract_parent_context",
    "span",
]

__version__ = "0.1.0"
