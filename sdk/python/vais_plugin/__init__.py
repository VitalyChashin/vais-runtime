"""vais-plugin — Python SDK for building Vais.Agents container plugins."""

import os
import warnings

from . import _import_guard
from . import _telemetry
from . import _log_handler

if os.environ.get("VAIS_PLUGIN_DISABLE_IMPORT_GUARD", "").strip() == "1":
    warnings.warn(
        "VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1: P12 import guard is disabled. "
        "Direct imports of LLM provider SDKs are allowed. Do not use in production.",
        stacklevel=1,
    )
else:
    _import_guard.install()

_telemetry._configure_otlp()
_log_handler._configure_log_handler()

from .agent import DeltaPayload, PluginAgent, SseEvent, ToolCompletedPayload, ToolStartedPayload, vais_plugin
from .gateway import AsyncLlmClient, AsyncToolClient, LlmResponse, ToolResult
from .models import (
    InvokeRequest,
    InvokeResponse,
    JournalEntry,
    LlmGatewayError,
    Message,
    OpaqueStateDeserializationError,
    PluginError,
    RequestContext,
    Timeout,
    ToolCall,
    ToolError,
    UsageCounts,
)

__all__ = [
    # agent
    "PluginAgent",
    "SseEvent",
    "DeltaPayload",
    "ToolStartedPayload",
    "ToolCompletedPayload",
    "vais_plugin",
    # gateway
    "AsyncLlmClient",
    "AsyncToolClient",
    "LlmResponse",
    "ToolResult",
    # models
    "InvokeRequest",
    "InvokeResponse",
    "JournalEntry",
    "Message",
    "OpaqueStateDeserializationError",
    "PluginError",
    "LlmGatewayError",
    "ToolError",
    "Timeout",
    "RequestContext",
    "ToolCall",
    "UsageCounts",
]
