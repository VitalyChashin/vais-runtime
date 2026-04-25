"""Hermetic two-node research graph (no real LLM — heuristic state machine).

Mimics a LangGraph-style graph with two nodes (plan → summarize) and a
routing edge. No external API calls; deterministic output for CI safety.
"""
from __future__ import annotations
from langgraph_researcher.state import ResearchState


def _node_plan(state: ResearchState) -> ResearchState:
    """Decompose the user input into research sub-questions (heuristic)."""
    words = state.user_input.lower().split()
    plan = [
        f"What is the definition of {words[0] if words else 'the topic'}?",
        f"What are the key aspects of {state.user_input}?",
        f"What are the practical implications of {state.user_input}?",
    ]
    return state.model_copy(update={"plan": plan})


def _node_summarize(state: ResearchState) -> ResearchState:
    """Produce a final summary from the plan (heuristic)."""
    plan_text = "\n".join(f"- {q}" for q in (state.plan or []))
    summary = (
        f"Research on '{state.user_input}' covered {len(state.plan or [])} questions:\n"
        f"{plan_text}\n"
        f"(Turn {state.turn_count + 1} — hermetic heuristic; no real LLM calls.)"
    )
    return state.model_copy(update={"summary": summary, "turn_count": state.turn_count + 1})


def _router(state: ResearchState) -> str:
    """Edge predicate: go to 'plan' if not planned yet, else 'summarize'."""
    return "summarize" if state.is_planned() else "plan"


def run_graph(state: ResearchState, new_user_input: str) -> ResearchState:
    """Execute the graph for one turn. Updates state.user_input, then routes."""
    state = state.model_copy(update={"user_input": new_user_input})
    next_node = _router(state)
    if next_node == "plan":
        state = _node_plan(state)
        # After planning, immediately summarize in the same turn.
        state = _node_summarize(state)
    else:
        # Already planned from a prior turn — just re-summarize.
        state = _node_summarize(state)
    return state
