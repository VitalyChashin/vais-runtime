"""Three-node LangGraph research graph with real web search.

Graph shape:
  START --_router--> [plan] --> [search] --> [summarize] --> END
                 \-> [summarize] --> END  (plan already exists)

LLM calls go through the VAIS LLM gateway (P12). Tool calls go through
the VAIS container gateway tools/invoke endpoint (P12). Gateway URL and
call token are threaded through ResearchState per invocation.
"""
from __future__ import annotations

import json
import re
from typing import Any

from langchain_core.callbacks import BaseCallbackHandler
from langchain_core.outputs import LLMResult
from langgraph.graph import END, START, StateGraph

from langgraph_researcher_live.state import ResearchState

_MODEL = "gpt-4o-mini"
_MAX_PLAN_TOKENS = 256
_MAX_SUMMARY_TOKENS = 768


class _TokenUsageCallback(BaseCallbackHandler):
    """Accumulates token usage across all LLM calls in a graph invocation."""

    def __init__(self) -> None:
        self.input_tokens = 0
        self.output_tokens = 0

    def on_llm_end(self, response: LLMResult, **kwargs) -> None:
        for gen_list in response.generations:
            for gen in gen_list:
                um = getattr(getattr(gen, "message", None), "usage_metadata", None) or {}
                self.input_tokens += um.get("input_tokens", 0)
                self.output_tokens += um.get("output_tokens", 0)


def _parse_json_array(text: str) -> list[str]:
    """Extract a JSON string array from LLM output, tolerating markdown fences."""
    cleaned = re.sub(r"```(?:json)?", "", text).strip().rstrip("`").strip()
    try:
        parsed = json.loads(cleaned)
        if isinstance(parsed, list):
            return [str(item) for item in parsed if item]
    except (json.JSONDecodeError, ValueError):
        pass
    lines = [re.sub(r"^[\-\*\d+\.\s]+", "", ln).strip() for ln in cleaned.splitlines()]
    return [ln for ln in lines if ln] or [text.strip()]


def _node_plan(state: ResearchState) -> dict[str, Any]:
    """Ask the model to decompose the topic into 3 research questions."""
    from vais_agent_sdk import ChatOpenAI
    llm = ChatOpenAI(
        model=_MODEL,
        max_tokens=_MAX_PLAN_TOKENS,
        llm_gateway_url=state.llm_gateway_url,
        call_token=state.call_token,
        run_id=state.run_id,
        agent_id=state.agent_id,
    )
    response = llm.invoke(
        f"You are a research planner. Break down this topic into exactly 3 specific "
        f"research questions that would help a researcher understand it thoroughly:\n\n"
        f"Topic: {state.user_input}\n\n"
        f"Reply with a JSON array of 3 question strings only, e.g. "
        f'["question 1", "question 2", "question 3"]. No other text.'
    )
    questions = _parse_json_array(str(response.content))
    return {"plan": questions}


async def _node_search(state: ResearchState) -> dict[str, Any]:
    """Invoke the Tavily search tool via the VAIS gateway for each plan question."""
    questions = state.plan or []
    if not questions:
        return {"search_results": []}

    from vais_agent_sdk import gateway_get_tools
    tools = await gateway_get_tools(
        tool_gateway_base_url=state.llm_gateway_url,
        call_token=state.call_token,
        run_id=state.run_id,
        agent_id=state.agent_id,
    )
    search_tool = next((t for t in tools if "search" in t.name), None)
    if search_tool is None:
        return {"search_results": []}

    results: list[str] = []
    tool_journal: list[dict] = []
    for question in questions:
        input_args = {"query": question, "max_results": 3}
        raw = await search_tool.ainvoke(input_args)
        results.append(str(raw))
        tool_journal.append({
            "toolName": search_tool.name,
            "inputJson": json.dumps(input_args),
            "outputJson": str(raw),
        })

    return {"search_results": results, "tool_journal": tool_journal}


def _node_summarize(state: ResearchState) -> dict[str, Any]:
    """Write a summary grounded in web search results when available."""
    from vais_agent_sdk import ChatOpenAI
    llm = ChatOpenAI(
        model=_MODEL,
        max_tokens=_MAX_SUMMARY_TOKENS,
        llm_gateway_url=state.llm_gateway_url,
        call_token=state.call_token,
        run_id=state.run_id,
        agent_id=state.agent_id,
    )
    plan_text = "\n".join(f"- {q}" for q in (state.plan or []))

    if state.search_results:
        pairs = "\n\n".join(
            f"Question: {q}\nSearch results:\n{r}"
            for q, r in zip(state.plan or [], state.search_results)
        )
        prompt = (
            f"You are a research summarizer. The user wants to understand:\n\n"
            f"  {state.user_input}\n\n"
            f"A researcher investigated these questions:\n{plan_text}\n\n"
            f"Web search findings:\n{pairs}\n\n"
            f"Write a concise 2-3 paragraph summary that directly addresses the topic "
            f"and synthesizes the search findings above."
        )
    else:
        prompt = (
            f"You are a research summarizer. The user wants to understand:\n\n"
            f"  {state.user_input}\n\n"
            f"A researcher has identified these questions to investigate:\n{plan_text}\n\n"
            f"Write a concise 2-3 paragraph summary that directly addresses the topic "
            f"and each of the research questions above."
        )

    response = llm.invoke(prompt)
    return {"summary": str(response.content), "turn_count": state.turn_count + 1}


def _router(state: ResearchState) -> str:
    """Route to 'plan' on first turn, 'summarize' on subsequent turns."""
    return "summarize" if state.is_planned() else "plan"


def _build_graph() -> Any:
    g: StateGraph = StateGraph(ResearchState)
    g.add_node("plan", _node_plan)
    g.add_node("search", _node_search)
    g.add_node("summarize", _node_summarize)
    g.add_conditional_edges(START, _router, {"plan": "plan", "summarize": "summarize"})
    g.add_edge("plan", "search")
    g.add_edge("search", "summarize")
    g.add_edge("summarize", END)
    return g.compile()


_compiled = _build_graph()


async def run_graph(
    state: ResearchState, new_user_input: str
) -> tuple[ResearchState, "_TokenUsageCallback"]:
    """Execute the graph for one turn; return updated state and token usage."""
    tracker = _TokenUsageCallback()
    updated = state.model_copy(update={
        "user_input": new_user_input,
        "plan": None,
        "search_results": [],
        "summary": None,
        "tool_journal": [],
    })
    result = await _compiled.ainvoke(updated, config={"callbacks": [tracker]})
    if not isinstance(result, ResearchState):
        result = ResearchState.model_validate(result)
    return result, tracker
