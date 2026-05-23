"""
ext-sessionsum-python — Python port of the ext-sessionsum-csharp sample.
Demonstrates host:container extension-authored session lifecycle handling using the vais_extension SDK:
log session open/close and, on close, summarize from the conversation history.

A real handler would persist a model-generated summary to a memory store. Close is best-effort:
a hard crash skips it, and idle grain cycles produce extra open/close pairs.
"""
import uvicorn
from vais_extension import SessionLifecycleHook, Host
from vais_extension.wire import SessionLifecycleContext


class SessionSummarizer(SessionLifecycleHook):
    async def on_session(self, context: SessionLifecycleContext, call_id: str) -> None:
        if context.phase == "opened":
            print(f"[ext-sessionsum] session opened agent={context.agent_id} session={context.session_id}")
            return
        first_user = next((t.text for t in (context.history or []) if t.role == "user"), None)
        summary = "(no user turn)" if first_user is None else (first_user[:80] + ("…" if len(first_user) > 80 else ""))
        print(f"[ext-sessionsum] session closing agent={context.agent_id} session={context.session_id} "
              f"turns={context.turn_count} summary=\"{summary}\"")


app = Host(
    extension_id="ext-sessionsum-python",
    version="0.1.0",
    target_api_version="0.30",
    handlers={"session-summary": SessionSummarizer()},
).fastapi

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
