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

    model_config = {"populate_by_name": True}
