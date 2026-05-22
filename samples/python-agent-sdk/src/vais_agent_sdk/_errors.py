"""Typed plugin errors, mapped by the runner to a distinct ``errorType`` over JSON-RPC.

Parity with the HTTP container SDK (``vais_plugin``): the runtime classifies these into the same
``LlmGatewayError`` / ``ToolError`` / ``Timeout`` error types (HTTP-side 502/503/504) so retry policy,
alerting, and telemetry can act on the failure class instead of a generic internal error.
"""
from __future__ import annotations


class PluginError(Exception):
    """Base for plugin errors the runner maps to a distinct ``errorType``."""


class LlmGatewayError(PluginError):
    """LLM gateway middleware chain failed (errorType ``LlmGatewayError``). Auto-raised by the
    section/LLM gateway helpers on an upstream non-2xx; may also be raised manually."""


class ToolError(PluginError):
    """Tool-layer failure surfaced to the runtime (errorType ``ToolError``). Auto-raised by the gateway
    tool helpers when a tool call cannot be dispatched; raise manually to give up on a tool that returned
    an error result."""


class Timeout(PluginError):
    """Invocation exceeded its ``timeoutSeconds`` budget (errorType ``Timeout``). Auto-raised by the
    runner when the budget elapses; may also be raised manually."""
