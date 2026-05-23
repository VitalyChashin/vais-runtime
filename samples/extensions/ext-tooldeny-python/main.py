"""
ext-tooldeny-python — Python port of the ext-tooldeny-csharp sample.
Demonstrates host:container extension-authored tool governance using the vais_extension SDK:
deny a fixed set of dangerous tools on the tool gateway.
"""
import uvicorn
from vais_extension import ToolGatewayMiddleware, Host
from vais_extension.wire import ToolGatewayContext, ToolGatewayPreResponse

DENIED = {"shell", "delete_file"}


class TenantToolDeny(ToolGatewayMiddleware):
    async def pre(self, context: ToolGatewayContext, call_id: str) -> ToolGatewayPreResponse:
        if context.tool_name in DENIED:
            print(f"[ext-tooldeny] denied tool={context.tool_name} agent={context.agent_id}")
            return ToolGatewayPreResponse(
                action="shortCircuit",
                error=f"ToolDenied: '{context.tool_name}' is blocked by the ext-tooldeny extension.",
            )
        return ToolGatewayPreResponse(action="next")


app = Host(
    extension_id="ext-tooldeny-python",
    version="0.1.0",
    target_api_version="0.30",
    handlers={"tool-deny": TenantToolDeny()},
).fastapi

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
