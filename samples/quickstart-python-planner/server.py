"""Quickstart container plugin — P12-compliant reference using the vais-plugin SDK."""

from vais_plugin import InvokeRequest, InvokeResponse, Message, PluginAgent, vais_plugin

SYSTEM_PROMPT = (
    "You decompose a user research query into exactly three sub-questions "
    "that together cover the topic. Reply with three lines, no preamble, "
    "one sub-question per line."
)


@vais_plugin("0.24")
class QuickstartPlanner(PluginAgent):
    async def invoke(self, request: InvokeRequest) -> InvokeResponse:
        user_text = next(
            (m.content or "" for m in reversed(request.messages) if m.role == "user"),
            "",
        )
        messages = [
            Message(role="system", content=SYSTEM_PROMPT),
            Message(role="user", content=user_text),
        ]
        reply = await request.llm.complete(messages)
        return InvokeResponse(assistant_message=reply.content or "")


if __name__ == "__main__":
    QuickstartPlanner().serve()
