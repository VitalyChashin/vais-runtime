"""SGR deep-research agent — wraps sgr-agent-core for use as a vais plugin.

SGR reads OPENAI_API_KEY and TAVILY_API_KEY from the environment.
Vais injects plugin secrets as VAIS_SECRET_<NAME>; this module bridges them
before importing any SGR code so the library sees them at import time.
"""
from __future__ import annotations

import os

# Bridge vais-injected secrets to the env var names SGR expects.
for _secret, _env in (
    ("VAIS_SECRET_OPENAI_API_KEY", "OPENAI_API_KEY"),
    ("VAIS_SECRET_TAVILY_API_KEY", "TAVILY_API_KEY"),
):
    if _secret in os.environ:
        os.environ.setdefault(_env, os.environ[_secret])

from openai.types.chat import ChatCompletionMessageParam
from sgr_agent_core import AgentFactory
from sgr_agent_core.agent_definition import AgentDefinition, LLMConfig, ToolDefinition
from sgr_agent_core.agents.sgr_agent import SGRAgent
from sgr_agent_core.tools import (
    AdaptPlanTool,
    CreateReportTool,
    ExtractPageContentTool,
    FinalAnswerTool,
    GeneratePlanTool,
    WebSearchTool,
)


def _make_toolkit() -> list:
    toolkit = [WebSearchTool, CreateReportTool, FinalAnswerTool, GeneratePlanTool, AdaptPlanTool]
    if tavily_key := os.environ.get("TAVILY_API_KEY"):
        # ToolDefinition extra fields are validated by ExtractPageContentConfig —
        # tavily_api_key must be passed this way, not at the AgentDefinition level.
        toolkit.insert(1, ToolDefinition(
            name=ExtractPageContentTool.tool_name,
            base_class=ExtractPageContentTool,
            tavily_api_key=tavily_key,
        ))
    return toolkit


def _get_agent_def() -> AgentDefinition:
    return AgentDefinition(
        name="sgr-analyst",
        base_class=SGRAgent,
        tools=_make_toolkit(),
        llm=LLMConfig(api_key=os.environ.get("OPENAI_API_KEY", "")),
    )


async def run_research(query: str) -> str | None:
    """Run the SGR agent on a research query and return the final answer."""
    task_messages: list[ChatCompletionMessageParam] = [
        {"role": "user", "content": query}
    ]
    agent = await AgentFactory.create(_get_agent_def(), task_messages)
    return await agent.execute()
