"""
Cross-language wire conformance: every Python Pydantic model must serialize to the same
canonical JSON fixture that the C# proxy produces for the same logical payload.
Fixtures live in contracts/extensions/wire-fixtures/ and are the shared oracle for both sides.
"""
import json
from pathlib import Path

from vais_extension.host import (
    _ErrorResponseBody,
    _GraphNodePostRequestBody,
    _GraphNodePostResponseBody,
    _GraphNodePreResponseBody,
    _LlmPostRequestBody,
    _LlmPostResponseBody,
    _LlmPreRequestBody,
    _LlmPreResponseBody,
    _LlmResponseBody,
    _PostRequestBody,
    _PostResponseBody,
    _PreRequestBody,
    _PreResponseBody,
    _ToolPostRequestBody,
    _ToolPostResponseBody,
    _ToolPreResponseBody,
)

_WF = Path(__file__).parents[3] / "contracts" / "extensions" / "wire-fixtures"


def _load(seam: str, variant: str) -> dict:
    return json.loads((_WF / seam / f"{variant}.json").read_text())


def _dump(model) -> dict:
    return json.loads(model.model_dump_json(by_alias=True, exclude_none=True))


# ── agentInput ────────────────────────────────────────────────────────────────

def test_agent_input_pre_request():
    body = _PreRequestBody(
        call_id="call-1",
        context={"agentId": "agent-1", "runId": "run-1", "nodeId": "node-1", "message": "hello"},
    )
    assert _dump(body) == _load("agent-input", "pre-request")


def test_agent_input_pre_response_mutate():
    body = _PreResponseBody(action="mutate", continuation_token="tok-1", context_patch={"color": "blue"})
    assert _dump(body) == _load("agent-input", "pre-response-mutate")


def test_agent_input_post_request():
    body = _PostRequestBody(call_id="call-1", continuation_token="tok-1")
    assert _dump(body) == _load("agent-input", "post-request")


def test_agent_input_post_response():
    body = _PostResponseBody(action="next")
    assert _dump(body) == _load("agent-input", "post-response")


# ── agentOutput ───────────────────────────────────────────────────────────────

def test_agent_output_pre_request():
    body = _PreRequestBody(
        call_id="call-1",
        context={
            "agentId": "agent-1",
            "runId": "run-1",
            "sessionId": "session-1",
            "outputTokens": 120,
            "inputTokens": 30,
        },
    )
    assert _dump(body) == _load("agent-output", "pre-request")


def test_agent_output_pre_response_mutate():
    body = _PreResponseBody(action="mutate", continuation_token="tok-1", context_patch={"color": "blue"})
    assert _dump(body) == _load("agent-output", "pre-response-mutate")


def test_agent_output_post_request():
    body = _PostRequestBody(call_id="call-1", continuation_token="tok-1")
    assert _dump(body) == _load("agent-output", "post-request")


def test_agent_output_post_response():
    body = _PostResponseBody(action="next")
    assert _dump(body) == _load("agent-output", "post-response")


# ── toolGateway ───────────────────────────────────────────────────────────────

def test_tool_gateway_pre_request():
    body = _PreRequestBody(
        call_id="call-1",
        context={
            "toolName": "search",
            "callId": "tool-call-1",
            "arguments": {"query": "hello"},
            "agentId": "agent-1",
            "runId": "run-1",
            "privilegeLevel": "standard",
            "allowedTools": ["search", "fetch"],
        },
    )
    assert _dump(body) == _load("tool-gateway", "pre-request")


def test_tool_gateway_pre_response_short_circuit():
    body = _ToolPreResponseBody(action="shortCircuit", result="cached result")
    assert _dump(body) == _load("tool-gateway", "pre-response-short-circuit")


def test_tool_gateway_post_request():
    body = _ToolPostRequestBody(call_id="call-1", continuation_token="tok-1", outcome_result="search result")
    assert _dump(body) == _load("tool-gateway", "post-request")


def test_tool_gateway_post_response_mutate():
    body = _ToolPostResponseBody(action="mutate", result="redacted")
    assert _dump(body) == _load("tool-gateway", "post-response-mutate")


# ── llmGateway ────────────────────────────────────────────────────────────────

def test_llm_gateway_pre_request():
    body = _LlmPreRequestBody(
        call_id="call-1",
        request={
            "messages": [{"role": "user", "content": "hello"}],
            "systemPrompt": "You are helpful.",
            "temperature": 0.7,
            "maxTokens": 256,
            "agentId": "agent-1",
            "runId": "run-1",
        },
    )
    assert _dump(body) == _load("llm-gateway", "pre-request")


def test_llm_gateway_pre_response_mutate():
    body = _LlmPreResponseBody(
        action="mutate",
        continuation_token="tok-1",
        response=_LlmResponseBody(text="synthetic reply", prompt_tokens=10, completion_tokens=5),
    )
    assert _dump(body) == _load("llm-gateway", "pre-response-mutate")


def test_llm_gateway_post_request():
    body = _LlmPostRequestBody(
        call_id="call-1",
        continuation_token="tok-1",
        response=_LlmResponseBody(text="model reply", prompt_tokens=10, completion_tokens=5),
    )
    assert _dump(body) == _load("llm-gateway", "post-request")


def test_llm_gateway_post_response_mutate():
    body = _LlmPostResponseBody(
        action="mutate",
        response=_LlmResponseBody(text="rewritten reply", prompt_tokens=10, completion_tokens=5),
    )
    assert _dump(body) == _load("llm-gateway", "post-response-mutate")


# ── errorInterceptor ──────────────────────────────────────────────────────────

def test_error_interceptor_request():
    body = _PreRequestBody(
        call_id="call-1",
        context={
            "agentId": "agent-1",
            "runId": "run-1",
            "nodeId": "node-1",
            "errorType": "InvalidOperation",
            "errorMessage": "something failed",
        },
    )
    assert _dump(body) == _load("error-interceptor", "request")


def test_error_interceptor_response():
    body = _ErrorResponseBody(message="friendly error")
    assert _dump(body) == _load("error-interceptor", "response")


# ── graphNode ─────────────────────────────────────────────────────────────────

def test_graph_node_pre_request():
    body = _PreRequestBody(
        call_id="call-1",
        context={
            "runId": "run-1",
            "nodeId": "node-1",
            "nodeKind": "llm",
            "agentId": "agent-1",
            "superStep": 0,
            "input": {"query": "hello"},
        },
    )
    assert _dump(body) == _load("graph-node", "pre-request")


def test_graph_node_pre_response_short_circuit():
    body = _GraphNodePreResponseBody(action="shortCircuit", continuation_token="tok-1", output={"result": "cached"})
    assert _dump(body) == _load("graph-node", "pre-response-short-circuit")


def test_graph_node_post_request():
    body = _GraphNodePostRequestBody(call_id="call-1", continuation_token="tok-1", output={"result": "node output"})
    assert _dump(body) == _load("graph-node", "post-request")


def test_graph_node_post_response_mutate():
    body = _GraphNodePostResponseBody(action="mutate", output={"result": "transformed"})
    assert _dump(body) == _load("graph-node", "post-response-mutate")


# ── sessionLifecycle ──────────────────────────────────────────────────────────

def test_session_lifecycle_request():
    body = _PreRequestBody(
        call_id="call-1",
        context={
            "agentId": "agent-1",
            "sessionId": "session-1",
            "phase": "closing",
            "turnCount": 2,
            "history": [
                {"role": "user", "text": "hi"},
                {"role": "assistant", "text": "hello"},
            ],
        },
    )
    assert _dump(body) == _load("session-lifecycle", "request")
