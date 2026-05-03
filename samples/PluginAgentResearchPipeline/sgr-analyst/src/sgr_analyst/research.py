"""SGR deep-research agent — wraps sgr-agent-core for use as a vais plugin.

SGR reads OPENAI_API_KEY and TAVILY_API_KEY from the environment.
Vais injects plugin secrets as VAIS_SECRET_<NAME>; this module bridges them
before importing any SGR code so the library sees them at import time.
"""
from __future__ import annotations

import logging
import os

_log = logging.getLogger(__name__)
logging.basicConfig(level=logging.INFO)

# Bridge vais-injected secrets to the env var names SGR expects.
for _secret, _env in (
    ("VAIS_SECRET_OPENAI_API_KEY", "OPENAI_API_KEY"),
    ("VAIS_SECRET_TAVILY_API_KEY", "TAVILY_API_KEY"),
):
    if _secret in os.environ:
        os.environ.setdefault(_env, os.environ[_secret])
        _log.info("SGR bridge: set %s from %s", _env, _secret)
    else:
        _log.warning("SGR bridge: %s not found in env", _secret)

_log.info(
    "SGR env check: OPENAI_API_KEY=%s TAVILY_API_KEY=%s",
    "set" if os.environ.get("OPENAI_API_KEY") else "MISSING",
    "set" if os.environ.get("TAVILY_API_KEY") else "MISSING",
)

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
    base = [CreateReportTool, FinalAnswerTool, GeneratePlanTool, AdaptPlanTool]
    tavily_key = os.environ.get("TAVILY_API_KEY")
    _log.info(
        "SGR toolkit: tavily_key=%s WebSearchTool.tool_name=%s",
        "set" if tavily_key else "MISSING",
        WebSearchTool.tool_name,
    )
    if tavily_key:
        web_def = ToolDefinition(name=WebSearchTool.tool_name, base_class=WebSearchTool, api_key=tavily_key)
        _log.info("SGR toolkit: web_def.tool_kwargs()=%s", web_def.tool_kwargs())
        return [
            web_def,
            ToolDefinition(name=ExtractPageContentTool.tool_name, base_class=ExtractPageContentTool, tavily_api_key=tavily_key),
        ] + base
    return [WebSearchTool] + base


def _get_agent_def() -> AgentDefinition:
    return AgentDefinition(
        name="sgr-analyst",
        base_class=SGRAgent,
        tools=_make_toolkit(),
        llm=LLMConfig(api_key=os.environ.get("OPENAI_API_KEY", "")),
    )


async def run_research(query: str) -> str | None:
    """Run the SGR agent on a research query and return the final answer."""
    _log.info("SGR run_research: query=%r", query[:120])
    agent_def = _get_agent_def()
    _log.info(
        "SGR agent_def: tools=%s",
        [(t.name, t.tool_kwargs()) for t in agent_def.tools],
    )
    task_messages: list[ChatCompletionMessageParam] = [
        {"role": "user", "content": query}
    ]
    agent = await AgentFactory.create(agent_def, task_messages)
    # sgr-agent-core 0.7.0: NextStepToolsBuilder wraps each tool in D_<ToolName>,
    # which gets tool_name="d_<toolname>" via BaseTool.__init_subclass__ instead of
    # inheriting the original. _action_phase looks up configs by that name, so
    # add aliased entries so api_key reaches WebSearchConfig(**kwargs).
    for key, val in list(agent.tool_configs.items()):
        agent.tool_configs[f"d_{key}"] = val
    _log.info("SGR agent created: tool_configs keys=%s", list(agent.tool_configs.keys()))
    return await agent.execute()
