"""Minimal Tavily MCP server — exposes a single 'search' tool via FastMCP."""
import os
from mcp.server.fastmcp import FastMCP
from tavily import TavilyClient

app = FastMCP("tavily-search", host="0.0.0.0", port=8000)
_tavily = TavilyClient(api_key=os.environ["TAVILY_API_KEY"])


@app.tool()
def search(query: str, max_results: int = 3) -> list[dict]:
    """Search the web for a query and return results with title, url, and content."""
    response = _tavily.search(query, max_results=max_results)
    return [
        {
            "title": r.get("title", ""),
            "url": r.get("url", ""),
            "content": r.get("content", ""),
        }
        for r in response.get("results", [])
    ]


if __name__ == "__main__":
    app.run(transport="streamable-http")
