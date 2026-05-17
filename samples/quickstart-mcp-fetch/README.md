# quickstart-mcp-fetch

Toy MCP fetch server used by [QUICKSTART.md](../../QUICKSTART.md) — a ~15-line FastMCP
script that exposes one tool, `fetch(url)`, over the MCP **streamableHttp** transport on
`http://localhost:3000/mcp`.

Built only as part of the quickstart's `docker-compose.yaml`. Not intended as a production
MCP fetch implementation — it has no JS rendering, no robots.txt handling, no caching,
no retries. For production use a real MCP fetch server (e.g. the reference Python
`mcp-server-fetch`) and front it with the runtime's MCP gateway.

## Why not use `mcp/fetch` directly?

The official `mcp/fetch` image (and the reference `mcp-server-fetch` PyPI package) only
speak the MCP **stdio** transport. The quickstart's runtime uses `streamableHttp` so it
can talk to the server over HTTP from inside its own container. Rather than running a
stdio↔HTTP bridge in front of `mcp/fetch`, this sample owns the whole server — fewer
moving parts, no third-party version mismatches, and easy to read end-to-end.
