"""
ext-log-python — Python port of the ext-log-csharp sample.
Demonstrates host:container middleware using the vais_extension SDK.
"""
import uvicorn
from vais_extension import AgentInputMiddleware, AgentOutputMiddleware, Host
from vais_extension.wire import AgentInputContext, AgentOutputContext, PreResponse, PostResponse


class LogInput(AgentInputMiddleware):
    async def pre(self, context: AgentInputContext, call_id: str) -> PreResponse:
        print(f"[ext-log] in  agent={context.agent_id} msg={context.message!r}")
        return PreResponse(action="next")


class LogOutput(AgentOutputMiddleware):
    async def pre(self, context: AgentOutputContext, call_id: str) -> PreResponse:
        print(f"[ext-log] out agent={context.agent_id} tokens={context.output_tokens or 0}")
        return PreResponse(action="next")


app = Host(
    extension_id="ext-log-python",
    version="0.1.0",
    target_api_version="0.30",
    handlers={"log-input": LogInput(), "log-output": LogOutput()},
).fastapi

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
