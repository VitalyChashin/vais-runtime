"""ResearcherAgent — bridges vais-agent-sdk to the heuristic LangGraph graph."""
from __future__ import annotations

from vais_agent_sdk import AgentRequest, AgentResponse
from langgraph_researcher.graph import run_graph
from langgraph_researcher.state import ResearchState


async def invoke(request: AgentRequest) -> AgentResponse:
    """Handle one vais/agent.invoke call."""
    if request.state:
        state = ResearchState.from_json(request.state)
    else:
        state = ResearchState.initial(request.user_message)

    state = run_graph(state, request.user_message)

    return AgentResponse(
        assistantMessage=state.summary or "No summary produced.",
        newState=state.to_json(),
    )
