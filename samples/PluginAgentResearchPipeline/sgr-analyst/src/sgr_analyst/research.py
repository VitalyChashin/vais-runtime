"""SGR deep-research agent — wraps sgr-agent-core for use as a vais plugin.

LLM calls go through the VAIS LLM gateway (P12) via LLMConfig.base_url.
Tool calls go through the VAIS container gateway (P12) via GatewayWebSearchTool.
No provider API keys are stored in the subprocess environment.
"""
from __future__ import annotations

import logging
from typing import TYPE_CHECKING, Any

import httpx
from openai.types.chat import ChatCompletionMessageParam
from sgr_agent_core import AgentFactory
from sgr_agent_core.agent_definition import AgentDefinition, LLMConfig, ToolDefinition
from sgr_agent_core.agents.sgr_agent import SGRAgent
from sgr_agent_core.tools import (
    AdaptPlanTool,
    CreateReportTool,
    FinalAnswerTool,
    GeneratePlanTool,
    WebSearchTool,
)

if TYPE_CHECKING:
    from sgr_agent_core.agent_definition import AgentConfig
    from sgr_agent_core.models import AgentContext

_log = logging.getLogger(__name__)
logging.basicConfig(level=logging.INFO)


class _GatewayWebSearchTool(WebSearchTool):
    """WebSearchTool that routes through the VAIS container gateway (P12)."""

    async def __call__(self, context: "AgentContext", config: "AgentConfig", **kwargs: Any) -> str:
        gateway_url: str = kwargs.get("gateway_url", "")
        call_token: str = kwargs.get("call_token", "")
        run_id: str = kwargs.get("run_id", "")
        agent_id: str = kwargs.get("agent_id", "")

        headers = {
            "Authorization": f"Bearer {call_token}",
            "X-Run-Id": run_id,
            "X-Agent-Id": agent_id,
        }
        payload = {
            "toolName": "tavily_search",
            "arguments": {"query": self.query, "max_results": self.max_results},
            "toolCallId": f"sgr-{abs(hash(self.query)) % 65536:04x}",
        }
        _log.info("Gateway web search: query=%r gateway=%s", self.query[:80], gateway_url)
        async with httpx.AsyncClient() as client:
            resp = await client.post(
                f"{gateway_url}/v1/container-gateway/tools/invoke",
                headers=headers,
                json=payload,
                timeout=60.0,
            )
            resp.raise_for_status()
            result: str = resp.json().get("content", "")

        context.searches_used += 1
        return result


def _make_toolkit(
    llm_gateway_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
) -> list:
    base = [CreateReportTool, FinalAnswerTool, GeneratePlanTool, AdaptPlanTool]
    if llm_gateway_url and call_token:
        web_def = ToolDefinition(
            name=WebSearchTool.tool_name,
            base_class=_GatewayWebSearchTool,
            gateway_url=llm_gateway_url,
            call_token=call_token,
            run_id=run_id,
            agent_id=agent_id,
        )
        return [web_def] + base
    return base


def _get_agent_def(
    llm_gateway_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
) -> AgentDefinition:
    return AgentDefinition(
        name="sgr-analyst",
        base_class=SGRAgent,
        tools=_make_toolkit(llm_gateway_url, call_token, run_id, agent_id),
        llm=LLMConfig(
            api_key=call_token,
            base_url=f"{llm_gateway_url}/v1/container-gateway",
        ),
    )


async def run_research(
    query: str,
    llm_gateway_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
) -> str | None:
    """Run the SGR agent on a research query and return the final answer."""
    _log.info("SGR run_research: query=%r", query[:120])
    agent_def = _get_agent_def(llm_gateway_url, call_token, run_id, agent_id)
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
    # add aliased entries so gateway kwargs reach _GatewayWebSearchTool(**kwargs).
    for key, val in list(agent.tool_configs.items()):
        agent.tool_configs[f"d_{key}"] = val
    _log.info("SGR agent created: tool_configs keys=%s", list(agent.tool_configs.keys()))
    return await agent.execute()
