"""
ext-mem0-python — Illustrative Mem0-style memory extension.

IMPORTANT: This is a simplified illustration of the pattern described in
research/extensions-contract-2026-05-18.md §9.5.
Production use requires a running Mem0 instance and proper credentials.

Pattern:
  - agentInput (load seam, hot path): fetch relevant memories for run_id and inject into message
  - agentOutput (extract seam, warm path): extract new memories from the LLM response
"""
import os
import uvicorn
from collections import defaultdict
from vais_extension import AgentInputMiddleware, AgentOutputMiddleware, Host
from vais_extension.wire import AgentInputContext, AgentOutputContext, PreResponse, PostResponse


# In-process per-runId store (illustrative — replace with real Mem0 client in production).
_memory_store: dict[str, list[str]] = defaultdict(list)


class LoadMemories(AgentInputMiddleware):
    """Injects stored memories into the message context before the LLM call."""

    async def pre(self, context: AgentInputContext, call_id: str) -> PreResponse:
        run_key = context.run_id or context.agent_id
        memories = _memory_store.get(run_key, [])
        if not memories:
            return PreResponse(action="next")

        memory_block = "\n".join(f"- {m}" for m in memories[-5:])  # last 5 memories
        return PreResponse(
            action="mutate",
            context_patch={"mem0.memories": memory_block},
        )


class ExtractMemories(AgentOutputMiddleware):
    """Extracts notable facts from LLM responses and stores them for future turns."""

    async def pre(self, context: AgentOutputContext, call_id: str) -> PreResponse:
        # In production: call Mem0 API to extract + store memories from the response.
        # Here we just store a placeholder to illustrate the pattern.
        run_key = context.run_id or context.agent_id
        _memory_store[run_key].append(f"turn:{call_id}")
        return PreResponse(action="next", continuation_token=run_key)

    async def post(self, call_id: str, continuation_token: str | None) -> PostResponse:
        return PostResponse()


app = Host(
    extension_id="ext-mem0-python",
    version="0.1.0",
    target_api_version="0.30",
    handlers={
        "load":    LoadMemories(),
        "extract": ExtractMemories(),
    },
).fastapi

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8081)
