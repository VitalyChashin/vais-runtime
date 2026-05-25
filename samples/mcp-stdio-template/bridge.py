"""Stdio -> streamableHttp bridge for a container MCP server.

Reads its stdio child command from ``$MCP_STDIO_CMD`` (e.g. ``python -m mcp_server_fetch``),
spawns it as an MCP stdio child, and re-exposes the same tools over streamableHttp on
``$MCP_BRIDGE_PORT`` (default 7000) at ``$MCP_BRIDGE_PATH`` (default ``/mcp``).

Health endpoint at ``$MCP_HEALTH_PATH`` (default ``/health``) returns 200 once the proxy
server is listening — the runtime's ``DockerContainerSupervisor`` polls this before marking
the container Ready.

Use this script verbatim. Override behavior by setting env vars in your manifest's
``spec.container.env`` block. See ``samples/mcp-fetch-container/`` for a concrete example.
"""
from __future__ import annotations

# Telemetry opt-in: when Vais__ContainerPlugin__CallTokenSecret is set, the runtime
# injects OTEL_EXPORTER_OTLP_ENDPOINT/PROTOCOL/HEADERS, OTEL_RESOURCE_ATTRIBUTES,
# VAIS_LOG_ENDPOINT, and VAIS_LOG_TOKEN into this container. The stdio child inherits
# them. Install opentelemetry-sdk + opentelemetry-exporter-otlp-proto-http to activate
# span forwarding; see docs/guides/deploy-a-stdio-mcp-server.md for details.

import os
import shlex

from fastmcp import Client, FastMCP
from fastmcp.client.transports import StdioTransport
from starlette.requests import Request
from starlette.responses import JSONResponse


def _build_proxy() -> FastMCP:
    cmd_str = os.environ.get("MCP_STDIO_CMD")
    if not cmd_str:
        raise RuntimeError("MCP_STDIO_CMD is required (e.g. 'python -m mcp_server_fetch').")
    cmd = shlex.split(cmd_str)

    # Forward selected env vars to the stdio child so users can pass server-specific
    # config without rebuilding the image. The bridge process's full environment is
    # inherited by default; we only need to explicitly handle vars users add via
    # spec.container.env that should reach the child.
    transport = StdioTransport(command=cmd[0], args=cmd[1:])
    client = Client(transport)
    server_name = os.environ.get("MCP_SERVER_NAME", "vais-mcp-bridge")
    proxy = FastMCP.as_proxy(client, name=server_name)

    @proxy.custom_route(os.environ.get("MCP_HEALTH_PATH", "/health"), methods=["GET"])
    async def _health(_request: Request) -> JSONResponse:
        # The supervisor only checks 2xx; the body is informational.
        return JSONResponse({"status": "ok", "server": server_name})

    return proxy


def main() -> None:
    proxy = _build_proxy()
    proxy.run(
        transport="http",
        host="0.0.0.0",
        port=int(os.environ.get("MCP_BRIDGE_PORT", "7000")),
        path=os.environ.get("MCP_BRIDGE_PATH", "/mcp"),
    )


if __name__ == "__main__":
    main()
