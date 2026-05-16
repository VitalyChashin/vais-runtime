"""
P12 import guard — blocks direct imports of LLM provider SDKs from plugin code.

Registered as a sys.meta_path finder in __init__.py unless the
VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1 environment variable is set.

Blocked modules (and their sub-modules):
  openai, anthropic, cohere, mistralai, litellm,
  langchain_openai, langchain_anthropic, google.generativeai

Allowed:
  Everything else, including google.protobuf, httpx, requests, aiohttp, etc.
"""

from __future__ import annotations

import importlib.abc
import importlib.machinery
import sys
import warnings
from typing import Sequence

# Exact top-level names to block (and all sub-modules).
_BLOCKED: frozenset[str] = frozenset([
    "openai",
    "anthropic",
    "cohere",
    "mistralai",
    "litellm",
    "langchain_openai",
    "langchain_anthropic",
    "google.generativeai",
])


def _is_blocked(fullname: str) -> bool:
    """Return True if fullname is a blocked module or a sub-module of one."""
    if fullname in _BLOCKED:
        return True
    # Match sub-modules: e.g. "google.generativeai.types" or "openai.resources"
    for blocked in _BLOCKED:
        if fullname.startswith(blocked + "."):
            return True
    return False


class _VaisImportGuard(importlib.abc.MetaPathFinder):
    """
    MetaPathFinder that raises ImportError for blocked LLM provider packages.

    Returning None from find_spec passes control to the next finder;
    raising ImportError stops the import immediately.
    """

    def find_spec(
        self,
        fullname: str,
        path: Sequence[str] | None,
        target: object = None,
    ) -> importlib.machinery.ModuleSpec | None:
        if _is_blocked(fullname):
            raise ImportError(
                f"P12 violation: direct import of '{fullname}' is not allowed from a Vais "
                f"plugin. Route LLM calls through the runtime's LLM gateway endpoint "
                f"(request.llm_gateway_url) and tool calls through the MCP gateway endpoint "
                f"(request.tool_gateway_url). "
                f"Set VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1 to bypass (development only)."
            )
        return None  # pass through to the next finder


_GUARD_INSTANCE = _VaisImportGuard()


def install() -> None:
    """
    Insert the guard at the front of sys.meta_path.
    No-op if it is already installed.
    """
    if _GUARD_INSTANCE not in sys.meta_path:
        sys.meta_path.insert(0, _GUARD_INSTANCE)


def uninstall() -> None:
    """Remove the guard (for testing or when the escape hatch is set)."""
    try:
        sys.meta_path.remove(_GUARD_INSTANCE)
    except ValueError:
        pass
