"""
ext-nodetrace-python — Python port of the ext-nodetrace-csharp sample.
Demonstrates host:container extension-authored graph node middleware using the vais_extension SDK:
trace per-node timing and short-circuit a node when its input carries a cache marker.

graphNode is a hot seam (per-node round-trip) — apply with --accept-latency-cost.
"""
import time
import uvicorn
from vais_extension import GraphNodeMiddleware, Host
from vais_extension.wire import GraphNodeContext, GraphNodePreResponse, GraphNodePostResponse

# Holds per-call start times keyed by call_id (the pre/post pair shares a call_id).
_starts: dict[str, float] = {}


class NodeTracer(GraphNodeMiddleware):
    async def pre(self, context: GraphNodeContext, call_id: str) -> GraphNodePreResponse:
        if context.input.get("cacheHit") is True:
            print(f"[ext-nodetrace] cache hit node={context.node_id} agent={context.agent_id} — short-circuiting body")
            return GraphNodePreResponse(action="shortCircuit", output={"lastAssistantText": "(cached)"})
        _starts[call_id] = time.monotonic()
        print(f"[ext-nodetrace] node start node={context.node_id} kind={context.node_kind} "
              f"agent={context.agent_id} step={context.super_step}")
        return GraphNodePreResponse(action="next")

    async def post(self, call_id: str, continuation_token: str | None, output: dict) -> GraphNodePostResponse:
        started = _starts.pop(call_id, None)
        elapsed_ms = int((time.monotonic() - started) * 1000) if started is not None else -1
        print(f"[ext-nodetrace] node done call={call_id} elapsedMs={elapsed_ms} outputKeys={list(output.keys())}")
        return GraphNodePostResponse(action="next")


app = Host(
    extension_id="ext-nodetrace-python",
    version="0.1.0",
    target_api_version="0.30",
    handlers={"node-trace": NodeTracer()},
).fastapi

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
