# Template Dockerfile for a container MCP server.
#
# Copy this directory into your project, then:
#   1. Add a `RUN pip install <your-stdio-mcp-package>` line below the fastmcp install.
#   2. In your McpServer manifest's spec.container.env, set
#      `MCP_STDIO_CMD: "<command to run your stdio MCP server>"`
#      (e.g. "python -m mcp_server_fetch" or "uvx mcp-server-filesystem /workspace").
#
# The bridge listens on port 7000 by default (override via spec.container.port +
# the matching MCP_BRIDGE_PORT env var) and exposes /mcp + /health.

FROM python:3.12-slim
WORKDIR /app

# Bridge runtime — keep this exact line; everything else above/below is yours.
RUN pip install --no-cache-dir fastmcp==3.3.1

# Add your stdio MCP server here, e.g.:
# RUN pip install --no-cache-dir mcp-server-fetch==2025.4.7
# Or pull a wheel from your private feed:
# RUN pip install --no-cache-dir --index-url <your-feed> <your-pkg>

COPY bridge.py .
EXPOSE 7000
CMD ["python", "bridge.py"]
