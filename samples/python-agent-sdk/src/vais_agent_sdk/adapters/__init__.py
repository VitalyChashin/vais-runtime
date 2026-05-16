"""Framework-side adapters that flatten ``RequestSections`` into provider-native shapes.

Each adapter renders a :class:`vais_agent_sdk.sections.RequestSections` into the shape
its target framework expects. The shipped reference is :mod:`vais_agent_sdk.adapters.openai`,
which mirrors what ``InvokeRequest.messages`` would have looked like before the plugin
opted into the section pipeline (so the default behaviour is fully reproducible).

Follow-on adapters (LangGraph state slots, LangChain ChatPromptTemplate parts, SGR
planner inputs) are tracked as separate issues — see ``epic:sectioned-context``.
"""
from vais_agent_sdk.adapters.openai import sections_to_openai_messages, sections_to_openai_request

__all__ = ["sections_to_openai_messages", "sections_to_openai_request"]
