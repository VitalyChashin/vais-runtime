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
    return AgentResponse(
        assistant_message=result or "No analysis produced.",
    )
