"""P12-compliant LangChain helpers — route LLM and tool calls through the VAIS gateway."""
from __future__ import annotations

from typing import Any

import httpx


def ChatOpenAI(
    *,
    model: str = "gpt-4o",
    llm_gateway_url: str,
    call_token: str,
    run_id: str = "",
    agent_id: str = "",
    **kwargs: Any,
) -> Any:
    """Return a ChatOpenAI instance routed through the VAIS LLM gateway."""
    from langchain_openai import ChatOpenAI as _ChatOpenAI  # type: ignore[import]

    return _ChatOpenAI(
        model=model,
        base_url=f"{llm_gateway_url}/v1/container-gateway",
        api_key=call_token,  # type: ignore[arg-type]
        default_headers={"X-Run-Id": run_id, "X-Agent-Id": agent_id},
        **kwargs,
    )


class _GatewayLangChainTool:
    """LangChain BaseTool that routes all calls through the VAIS container gateway.

    Uses a dict args_schema (JSON Schema) instead of a Pydantic model so that
    ``tool.ainvoke({"query": ..., "max_results": ...})`` passes kwargs directly
    to _arun without LangChain's Pydantic schema inference getting in the way.
    An extra='allow' Pydantic model with no declared fields is treated by LangChain
    as a no-arg tool (empty get_fields() → early-exit returns ((), {})).
    """

    @staticmethod
    def build(
        *,
        name: str,
        description: str,
        parameters_schema: dict | None,
        tool_gateway_base_url: str,
        call_token: str,
        run_id: str,
        agent_id: str,
    ) -> Any:
        from langchain_core.tools.base import BaseTool  # type: ignore[import]
        from pydantic import Field, PrivateAttr

        schema = parameters_schema or {"type": "object", "additionalProperties": True}
        _name, _desc = name, description
        _base, _tok, _rid, _aid = tool_gateway_base_url, call_token, run_id, agent_id

        class _Impl(BaseTool):
            name: str = Field(default=_name)
            description: str = Field(default=_desc)
            args_schema: Any = Field(default=schema)
            _base_url: str = PrivateAttr(default=_base)
            _token: str = PrivateAttr(default=_tok)
            _run_id: str = PrivateAttr(default=_rid)
            _agent_id: str = PrivateAttr(default=_aid)

            def _run(self, **kwargs: Any) -> str:
                raise NotImplementedError("async only")

            async def _arun(self, **kwargs: Any) -> str:
                headers = {
                    "Authorization": f"Bearer {self._token}",
                    "X-Run-Id": self._run_id,
                    "X-Agent-Id": self._agent_id,
                }
                payload = {
                    "toolName": self.name,
                    "arguments": kwargs,
                    "toolCallId": f"lc-{self.name}",
                }
                async with httpx.AsyncClient() as client:
                    resp = await client.post(
                        f"{self._base_url}/v1/container-gateway/tools/invoke",
                        headers=headers,
                        json=payload,
                        timeout=60.0,
                    )
                    resp.raise_for_status()
                    return resp.json().get("content", "")

        return _Impl()


async def gateway_get_tools(
    tool_gateway_base_url: str,
    call_token: str,
    run_id: str,
    agent_id: str,
) -> list[Any]:
    """Fetch available tools from the VAIS gateway and return LangChain-compatible tools.

    Each returned tool's ``ainvoke({"key": value, ...})`` call dispatches directly to
    the gateway's tools/invoke endpoint with the correct auth headers.
    """
    headers = {
        "Authorization": f"Bearer {call_token}",
        "X-Run-Id": run_id,
        "X-Agent-Id": agent_id,
    }
    async with httpx.AsyncClient() as client:
        resp = await client.get(
            f"{tool_gateway_base_url}/v1/container-gateway/tools/list",
            headers=headers,
            timeout=30.0,
        )
        resp.raise_for_status()
        data = resp.json()

    return [
        _GatewayLangChainTool.build(
            name=t["name"],
            description=t.get("description", ""),
            parameters_schema=t.get("parametersSchema"),
            tool_gateway_base_url=tool_gateway_base_url,
            call_token=call_token,
            run_id=run_id,
            agent_id=agent_id,
        )
        for t in data.get("tools", [])
    ]
