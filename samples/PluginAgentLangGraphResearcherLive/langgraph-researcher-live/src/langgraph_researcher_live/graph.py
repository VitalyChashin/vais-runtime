"""Two-node LangGraph research graph backed by a real Claude LLM.

Graph shape mirrors the hermetic sibling (PluginAgentLangGraphResearcher):
  START --_router--> [plan] --> [summarize] --> END
                 \-> [summarize] --> END  (plan already exists)

Prerequisites:
  Declare the secret in plugin.yaml:
    spec:
      secrets:
        OPENAI_API_KEY: "secret://env/OPENAI_API_KEY"
  The VAIS runtime resolves this at startup (v0.31) and injects
  VAIS_SECRET_OPENAI_API_KEY into the subprocess environment.
"""
from __future__ import annotations

import json
import os
import re
from typing import Any

from langchain_openai import ChatOpenAI
from langgraph.graph import END, START, StateGraph

from langgraph_researcher_live.state import ResearchState

_MODEL = "gpt-4o-mini"
_MAX_TOKENS = 512
_OPENAI_API_KEY = os.environ["VAIS_SECRET_OPENAI_API_KEY"]


def _parse_json_array(text: str) -> list[str]:
    """Extract a JSON string array from LLM output, tolerating markdown fences."""
    cleaned = re.sub(r"```(?:json)?", "", text).strip().rstrip("`").strip()
    try:
        parsed = json.loads(cleaned)
        if isinstance(parsed, list):
            return [str(item) for item in parsed if item]
    except (json.JSONDecodeError, ValueError):
        pass
    # Fallback: split on newlines and strip common bullet prefixes.
    lines = [re.sub(r"^[\-\*\d+\.\s]+", "", ln).strip() for ln in cleaned.splitlines()]
    return [ln for ln in lines if ln] or [text.strip()]


def _node_plan(state: ResearchState) -> dict[str, Any]:
    """Ask Claude to decompose the topic into 3 research questions."""
    llm = ChatOpenAI(model=_MODEL, max_tokens=_MAX_TOKENS, api_key=_OPENAI_API_KEY)
    response = llm.invoke(
        f"You are a research planner. Break down this topic into exactly 3 specific "
        f"research questions that would help a researcher understand it thoroughly:\n\n"
        f"Topic: {state.user_input}\n\n"
        f"Reply with a JSON array of 3 question strings only, e.g. "
        f'["question 1", "question 2", "question 3"]. No other text.'
    )
    questions = _parse_json_array(str(response.content))
    return {"plan": questions}


def _node_summarize(state: ResearchState) -> dict[str, Any]:
    """Ask Claude to write a concise summary covering the research plan."""
    llm = ChatOpenAI(model=_MODEL, max_tokens=_MAX_TOKENS, api_key=_OPENAI_API_KEY)
    plan_text = "\n".join(f"- {q}" for q in (state.plan or []))
    response = llm.invoke(
        f"You are a research summarizer. The user wants to understand:\n\n"
        f"  {state.user_input}\n\n"
        f"A researcher has identified these questions to investigate:\n{plan_text}\n\n"
        f"Write a concise 2-3 paragraph summary that directly addresses the topic "
        f"and each of the research questions above."
    )
    return {"summary": str(response.content), "turn_count": state.turn_count + 1}


def _router(state: ResearchState) -> str:
    """Route to 'plan' on first turn, 'summarize' on subsequent turns."""
    return "summarize" if state.is_planned() else "plan"


def _build_graph() -> Any:
    g: StateGraph = StateGraph(ResearchState)
    g.add_node("plan", _node_plan)
    g.add_node("summarize", _node_summarize)
    g.add_conditional_edges(START, _router, {"plan": "plan", "summarize": "summarize"})
    g.add_edge("plan", "summarize")
    g.add_edge("summarize", END)
    return g.compile()


_compiled = _build_graph()


def run_graph(state: ResearchState, new_user_input: str) -> ResearchState:
    """Execute the graph for one turn and return the updated state."""
    updated = state.model_copy(update={"user_input": new_user_input})
    result = _compiled.invoke(updated)
    if isinstance(result, ResearchState):
        return result
    return ResearchState.model_validate(result)
