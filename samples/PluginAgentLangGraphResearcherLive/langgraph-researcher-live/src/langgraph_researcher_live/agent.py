"""ResearcherAgent — bridges vais-agent-sdk to the live LangGraph graph."""
from __future__ import annotations

from vais_agent_sdk import AgentJournalEntry, AgentRequest, AgentResponse, AgentUsage
from langgraph_researcher_live.graph import _MODEL, run_graph
from langgraph_researcher_live.state import ResearchState


def _apply_gateway_context(state: ResearchState, request: AgentRequest) -> ResearchState:
    return state.model_copy(update={
        "llm_gateway_url": request.llm_gateway_url,
        "call_token": request.call_token,
        "run_id": request.run_id,
        "agent_id": request.agent_id,
    })


async def invoke(request: AgentRequest) -> AgentResponse:
    """Handle one vais/agent.invoke call."""
    if request.state:
        state = ResearchState.from_json(request.state)
    else:
        state = ResearchState.initial(request.user_message)

    state = _apply_gateway_context(state, request)
    state, tracker = await run_graph(state, request.user_message)

    usage = None
    if tracker.input_tokens > 0 or tracker.output_tokens > 0:
        usage = [AgentUsage(
            model=_MODEL,
            input_tokens=tracker.input_tokens,
            output_tokens=tracker.output_tokens,
        )]

    journal = [
        AgentJournalEntry(toolName=e["toolName"], inputJson=e["inputJson"], outputJson=e["outputJson"])
        for e in state.tool_journal
    ] or None

    return AgentResponse(
        assistantMessage=state.summary or "No summary produced.",
        newState=state.to_json(),
        usage=usage,
        journal=journal,
    )
