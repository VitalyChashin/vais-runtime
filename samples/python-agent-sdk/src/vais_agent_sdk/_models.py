"""Wire types for the vais/agent.invoke JSON-RPC method (v0.24)."""
from __future__ import annotations
from typing import Optional
from pydantic import BaseModel, Field


class AgentRequest(BaseModel):
    agent_id: str = Field(alias="agentId")
    session_id: str = Field(alias="sessionId")
    user_message: str = Field(alias="userMessage")
    state: Optional[str] = Field(default=None)
    timeout_seconds: int = Field(default=60, alias="timeoutSeconds")
    context: Optional[dict[str, str]] = Field(default=None)

    model_config = {"populate_by_name": True}

    @property
    def llm_gateway_url(self) -> str:
        return (self.context or {}).get("llmGatewayUrl", "")

    @property
    def call_token(self) -> str:
        return (self.context or {}).get("callToken", "")

    @property
    def run_id(self) -> str:
        return (self.context or {}).get("runId", "")


class AgentUsage(BaseModel):
    model: str
    input_tokens: int = Field(alias="inputTokens")
    output_tokens: int = Field(alias="outputTokens")

    model_config = {"populate_by_name": True}


class AgentJournalEntry(BaseModel):
    tool_name: str = Field(alias="toolName")
    input_json: str = Field(alias="inputJson")
    output_json: str = Field(alias="outputJson")

    model_config = {"populate_by_name": True}


class AgentResponse(BaseModel):
    assistant_message: str = Field(alias="assistantMessage")
    new_state: Optional[str] = Field(default=None, alias="newState")
    usage: Optional[list[AgentUsage]] = Field(default=None)
    journal: Optional[list[AgentJournalEntry]] = Field(default=None)
    # Degraded-result signal (P9): set is_partial=True when you produced some content but an
    # internal step failed or was incomplete. The runtime marks the turn WARNING (not a clean
    # success, not a failure) so it shows up in run-health diagnostics instead of looking green.
    # For a hard, unrecoverable failure, `raise` a classified error (LlmGatewayError / ToolError /
    # Timeout) instead — never return placeholder text as if it succeeded.
    # Optional (default None) so a clean response omits these from the wire under
    # model_dump(exclude_none=True) — keeps the success shape unchanged and symmetric with the
    # vais_plugin SDK, which also emits them only when partial. C# AgentInvokeResponse.IsPartial
    # defaults to false when the field is absent.
    is_partial: Optional[bool] = Field(default=None, alias="isPartial")
    failure_reason: Optional[str] = Field(default=None, alias="failureReason")

    model_config = {"populate_by_name": True}
