"""SGR analyst agent — bridges vais-agent-sdk to the SGR research agent."""
from __future__ import annotations

from vais_agent_sdk import AgentRequest, AgentResponse
from sgr_analyst.research import run_research


async def invoke(request: AgentRequest) -> AgentResponse:
    """Handle one vais/agent.invoke call."""
    result = await run_research(request.user_message)
    return AgentResponse(
        assistant_message=result or "No analysis produced.",
    )
