"""VAIS Agent SDK — Python-side runtime for vais/agent.* JSON-RPC protocol."""
from vais_agent_sdk._models import AgentRequest, AgentResponse, AgentUsage, AgentJournalEntry
from vais_agent_sdk._runner import run, StreamFn
from vais_agent_sdk.adapters import sections_to_openai_messages, sections_to_openai_request
from vais_agent_sdk.langchain_compat import ChatOpenAI, gateway_get_tools
from vais_agent_sdk.sections import (
    CompletionResult,
    CompletionUsage,
    RequestSections,
    Section,
    SectionBudget,
    SectionPayload,
    build_sections,
    complete_from_sections,
)

__all__ = [
    "AgentRequest", "AgentResponse", "AgentUsage", "AgentJournalEntry",
    "run", "StreamFn",
    "ChatOpenAI", "gateway_get_tools",
    "build_sections", "complete_from_sections",
    "RequestSections", "Section", "SectionBudget", "SectionPayload",
    "CompletionResult", "CompletionUsage",
    "sections_to_openai_messages", "sections_to_openai_request",
]
