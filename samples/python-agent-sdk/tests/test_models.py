"""Unit tests for AgentRequest / AgentResponse wire types."""
from __future__ import annotations

import pytest
from pydantic import ValidationError

from vais_agent_sdk._models import AgentRequest, AgentResponse, AgentUsage, AgentJournalEntry


def test_agent_request_camel_case_alias():
    req = AgentRequest.model_validate({
        "agentId": "agent-1",
        "sessionId": "sess-1",
        "userMessage": "hello",
    })
    assert req.agent_id == "agent-1"
    assert req.session_id == "sess-1"
    assert req.user_message == "hello"
    assert req.state is None
    assert req.timeout_seconds == 60


def test_agent_request_with_state():
    req = AgentRequest.model_validate({
        "agentId": "a",
        "sessionId": "s",
        "userMessage": "hi",
        "state": '{"count": 1}',
    })
    assert req.state == '{"count": 1}'


def test_agent_response_serializes_camel_case():
    resp = AgentResponse(assistantMessage="Hello!")
    d = resp.model_dump(by_alias=True, exclude_none=True)
    assert d == {"assistantMessage": "Hello!"}


def test_agent_response_with_state_and_usage():
    resp = AgentResponse(
        assistantMessage="Done.",
        newState='{"step": 2}',
        usage=[AgentUsage(model="claude-3", inputTokens=10, outputTokens=5)],
    )
    d = resp.model_dump(by_alias=True, exclude_none=True)
    assert d["newState"] == '{"step": 2}'
    assert d["usage"][0] == {"model": "claude-3", "inputTokens": 10, "outputTokens": 5}


def test_agent_request_missing_required_fields():
    with pytest.raises(ValidationError):
        AgentRequest.model_validate({"agentId": "a"})  # missing sessionId, userMessage
