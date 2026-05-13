"""Typed state shared across graph nodes."""
from __future__ import annotations
from typing import Optional
from pydantic import BaseModel


class ResearchState(BaseModel):
    user_input: str = ""
    plan: Optional[list[str]] = None
    search_results: list[str] = []
    summary: Optional[str] = None
    turn_count: int = 0
    tool_journal: list[dict] = []
    llm_gateway_url: str = ""
    call_token: str = ""
    run_id: str = ""
    agent_id: str = ""

    def is_planned(self) -> bool:
        return self.plan is not None

    def to_json(self) -> str:
        return self.model_dump_json()

    @classmethod
    def from_json(cls, blob: str) -> "ResearchState":
        return cls.model_validate_json(blob)

    @classmethod
    def initial(cls, user_input: str) -> "ResearchState":
        return cls(user_input=user_input)
