"""SGR analyst agent — bridges vais-agent-sdk to the SGR research agent."""
from __future__ import annotations

from vais_agent_sdk import AgentRequest, AgentResponse
from sgr_analyst.research import run_research


async def invoke(request: AgentRequest) -> AgentResponse:
    """Handle one vais/agent.invoke call."""
    result = await run_research(
        query=request.user_message,
        llm_gateway_url=request.llm_gateway_url,
        call_token=request.call_token,
        run_id=request.run_id,
        agent_id=request.agent_id,
    )
    # run_research returns None/empty when the SGR engine produced no analysis (an internal failure
    # it swallowed). Signal that as a *partial* result instead of returning placeholder text as if it
    # succeeded — so the runtime marks the turn WARNING and it surfaces in run-health diagnostics
    # rather than looking green. (A hard, unrecoverable failure should `raise` a classified error —
    # LlmGatewayError / ToolError / Timeout — instead.)
    if not result:
        return AgentResponse(
            assistant_message="No analysis produced.",
            is_partial=True,
            failure_reason="SGR engine returned no analysis (run_research produced an empty result).",
        )
    return AgentResponse(assistant_message=result)
