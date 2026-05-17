"""Toy MCP fetch server used by QUICKSTART.md.

Exposes a single `fetch(url)` tool over MCP streamableHttp on `:3000/mcp`.
Implementation is deliberately minimal — no JS rendering, no robots.txt, no
retries — it just GETs the URL and returns the body as text. Use a real MCP
fetch implementation in production.
"""
from fastmcp import FastMCP
import httpx

mcp = FastMCP("quickstart-fetch")


@mcp.tool
async def fetch(url: str) -> str:
    """Fetch a URL and return its body as text (truncated to 8 KiB)."""
    async with httpx.AsyncClient(follow_redirects=True, timeout=10.0) as client:
        r = await client.get(url, headers={"User-Agent": "vais-quickstart-fetch/1.0"})
        r.raise_for_status()
        return r.text[:8192]


if __name__ == "__main__":
    mcp.run(transport="http", host="0.0.0.0", port=3000, path="/mcp")
