"""
ext-errortag-python — Python port of the ext-errortag-csharp sample.
Demonstrates host:container extension-authored error handling using the vais_extension SDK:
tag and audit agent/graph failure messages on the errorInterceptor seam.

Only the human-facing message is rewritten — error_type is never changed and the failure is never
suppressed (P9).
"""
import uvicorn
from vais_extension import ErrorInterceptor, Host
from vais_extension.wire import ErrorContext, ErrorOutcome


class TenantErrorTag(ErrorInterceptor):
    async def on_error(self, context: ErrorContext, call_id: str) -> ErrorOutcome:
        print(
            f"[ext-errortag] failure agent={context.agent_id} run={context.run_id} "
            f"node={context.node_id} type={context.error_type}: {context.error_message}"
        )
        ref = context.run_id or "n/a"
        return ErrorOutcome(message=f"[acme-corp] {context.error_message} (ref: {ref})")


app = Host(
    extension_id="ext-errortag-python",
    version="0.1.0",
    target_api_version="0.33",
    handlers={"error-tag": TenantErrorTag()},
).fastapi

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
