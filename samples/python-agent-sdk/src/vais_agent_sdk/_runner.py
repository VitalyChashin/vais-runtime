"""JSON-RPC 2.0 over stdio dispatcher for vais/agent.* methods (v0.26).

The MCP stdio transport uses newline-delimited JSON (one JSON object per line,
no Content-Length framing). This runner handles the initial MCP handshake
(initialize + tools/list) plus the custom vais/agent.invoke, vais/agent.stream,
and vais/agent.reset methods dispatched to user-supplied coroutines.

Cross-platform design: stdin is read synchronously line-by-line; each message
is dispatched via asyncio.run() so user coroutines can be async. This avoids
ProactorEventLoop / IOCP limitations on Windows.
"""
from __future__ import annotations

import asyncio
import json
import sys
import traceback
from typing import Any, AsyncGenerator, Awaitable, Callable, Optional

from vais_agent_sdk._models import AgentRequest, AgentResponse

_PROTOCOL_VERSION = "2024-11-05"
_SERVER_INFO = {"name": "vais-agent-sdk", "version": "0.26.0"}

InvokeFn = Callable[[AgentRequest], Awaitable[AgentResponse]]
ResetFn = Callable[[str], Awaitable[None]]
StreamFn = Callable[[AgentRequest], AsyncGenerator[str, None]]


def _result(id_: Any, result: Any) -> str:
    return json.dumps({"jsonrpc": "2.0", "id": id_, "result": result})


def _error(id_: Any, code: int, message: str) -> str:
    return json.dumps({"jsonrpc": "2.0", "id": id_, "error": {"code": code, "message": message}})


def _send(line: str) -> None:
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def _notification(method: str, params: Any) -> str:
    return json.dumps({"jsonrpc": "2.0", "method": method, "params": params})


async def _dispatch(
    msg: dict,
    invoke_fn: InvokeFn,
    reset_fn: Optional[ResetFn],
    stream_fn: Optional[StreamFn],
) -> None:
    """Dispatch one JSON-RPC message and write the response to stdout."""
    method = msg.get("method", "")
    id_ = msg.get("id")          # None for notifications
    params = msg.get("params") or {}

    if method == "initialize":
        _send(_result(id_, {
            "protocolVersion": _PROTOCOL_VERSION,
            "capabilities": {"tools": {}},
            "serverInfo": _SERVER_INFO,
        }))

    elif method == "initialized":
        pass  # notification — no response

    elif method == "ping":
        if id_ is not None:
            _send(_result(id_, {}))

    elif method == "tools/list":
        # Pure agent-handler plugins expose no MCP tools.
        _send(_result(id_, {"tools": []}))

    elif method == "vais/agent.invoke":
        try:
            request = AgentRequest.model_validate(params)
            response = await invoke_fn(request)
            _send(_result(id_, response.model_dump(by_alias=True, exclude_none=True)))
        except Exception as exc:  # noqa: BLE001
            print(f"[vais-agent-sdk] invoke error:\n{traceback.format_exc()}", file=sys.stderr)
            _send(_error(id_, -32000,
                f"[python-agent-invoke-failed] {type(exc).__name__}: {exc}"))

    elif method == "vais/agent.stream":
        if stream_fn is None:
            # Fall back: run invoke and bundle the single text chunk in the response.
            try:
                request = AgentRequest.model_validate(params)
                response = await invoke_fn(request)
                result_dict = response.model_dump(by_alias=True, exclude_none=True)
                result_dict["deltas"] = [response.assistant_message]
                _send(_result(id_, result_dict))
            except Exception as exc:  # noqa: BLE001
                print(f"[vais-agent-sdk] stream error:\n{traceback.format_exc()}", file=sys.stderr)
                _send(_error(id_, -32000,
                    f"[python-agent-stream-failed] {type(exc).__name__}: {exc}"))
        else:
            try:
                request = AgentRequest.model_validate(params)
                chunks: list[str] = []
                async for chunk in stream_fn(request):
                    chunks.append(chunk)
                assistant_message = "".join(chunks)
                response = AgentResponse(
                    assistant_message=assistant_message,
                    new_state=None,
                    usage=None,
                    journal=None,
                )
                result_dict = response.model_dump(by_alias=True, exclude_none=True)
                result_dict["deltas"] = chunks
                _send(_result(id_, result_dict))
            except Exception as exc:  # noqa: BLE001
                print(f"[vais-agent-sdk] stream error:\n{traceback.format_exc()}", file=sys.stderr)
                _send(_error(id_, -32000,
                    f"[python-agent-stream-failed] {type(exc).__name__}: {exc}"))

    elif method == "vais/agent.reset":
        session_id: str = params.get("sessionId", "")
        try:
            if reset_fn is not None:
                await reset_fn(session_id)
            _send(_result(id_, {}))
        except Exception as exc:  # noqa: BLE001
            print(f"[vais-agent-sdk] reset error:\n{traceback.format_exc()}", file=sys.stderr)
            _send(_error(id_, -32000,
                f"[python-agent-invoke-failed] {type(exc).__name__}: {exc}"))

    else:
        if id_ is not None:
            _send(_error(id_, -32601, f"Method not found: {method}"))


def run(
    invoke: InvokeFn,
    *,
    on_reset: Optional[ResetFn] = None,
    on_stream: Optional[StreamFn] = None,
) -> None:
    """Start the JSON-RPC dispatcher.

    Reads newline-delimited JSON-RPC 2.0 messages from stdin (one per line)
    and writes responses to stdout. Blocks until stdin closes (subprocess exit).

    Cross-platform: uses synchronous line-by-line stdin reading so it works on
    Windows (ProactorEventLoop) and Unix (SelectorEventLoop) without special
    configuration. Each message is dispatched via ``asyncio.run()`` so user
    coroutines can freely use ``async/await``.

    Args:
        invoke: Async function called for every ``vais/agent.invoke`` request.
                Must accept an :class:`AgentRequest` and return an
                :class:`AgentResponse`.
        on_reset: Optional async function called for ``vais/agent.reset``.
                  Receives the ``session_id`` string. Defaults to a no-op.
        on_stream: Optional async generator called for ``vais/agent.stream``.
                   Must accept an :class:`AgentRequest` and yield ``str`` chunks.
                   When omitted, ``vais/agent.stream`` falls back to ``invoke``
                   and emits a single delta.
    """
    # Ensure stdout is in text mode (may be binary on some platforms).
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(line_buffering=True)  # type: ignore[union-attr]

    for raw in sys.stdin:
        line = raw.rstrip("\n\r")
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as exc:
            print(f"[vais-agent-sdk] JSON parse error: {exc}", file=sys.stderr)
            continue
        asyncio.run(_dispatch(msg, invoke, on_reset, on_stream))
