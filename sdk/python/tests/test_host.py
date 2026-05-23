"""Tests for the registry-driven vais_extension FastAPI Host seam routing.

These pin the Host's routing (one ``_SeamSpec`` row per seam instead of an isinstance
chain) so adding the tool/llm seams later cannot silently break agentInput/agentOutput
dispatch, and pin the camelCase envelope contract the C# runtime proxy speaks
(``callId`` / ``continuationToken`` / ``contextPatch``).
"""
from __future__ import annotations

from fastapi.testclient import TestClient

from vais_extension import (
    AgentInputMiddleware,
    AgentOutputMiddleware,
    Host,
    LlmGatewayMiddleware,
    ToolGatewayMiddleware,
)
from vais_extension.wire import (
    AgentInputContext,
    AgentOutputContext,
    LlmContext,
    LlmGatewayPostResponse,
    LlmGatewayPreResponse,
    LlmMessage,
    LlmResponse,
    PostResponse,
    PreResponse,
    ToolGatewayContext,
    ToolGatewayPostResponse,
    ToolGatewayPreResponse,
    ToolOutcome,
)


class _RecordingInput(AgentInputMiddleware):
    def __init__(self) -> None:
        self.pre_ctx: AgentInputContext | None = None
        self.pre_call_id: str | None = None
        self.post_args: tuple[str, str | None] | None = None

    async def pre(self, context: AgentInputContext, call_id: str) -> PreResponse:
        self.pre_ctx = context
        self.pre_call_id = call_id
        return PreResponse(action="mutate", continuation_token="ct-1", context_patch={"k": "v"})

    async def post(self, call_id: str, continuation_token: str | None) -> PostResponse:
        self.post_args = (call_id, continuation_token)
        return PostResponse(action="mutate", context_patch={"after": 1})


class _ShortCircuitOutput(AgentOutputMiddleware):
    def __init__(self) -> None:
        self.pre_ctx: AgentOutputContext | None = None

    async def pre(self, context: AgentOutputContext, call_id: str) -> PreResponse:
        self.pre_ctx = context
        return PreResponse(action="shortCircuit")


class _RecordingTool(ToolGatewayMiddleware):
    def __init__(self, pre_action: str = "next", pre_error: str | None = None,
                 post_action: str = "next", post_result: str | None = None) -> None:
        self._pre_action = pre_action
        self._pre_error = pre_error
        self._post_action = post_action
        self._post_result = post_result
        self.pre_ctx: ToolGatewayContext | None = None
        self.post_outcome: ToolOutcome | None = None

    async def pre(self, context: ToolGatewayContext, call_id: str) -> ToolGatewayPreResponse:
        self.pre_ctx = context
        return ToolGatewayPreResponse(action=self._pre_action, error=self._pre_error)

    async def post(self, call_id: str, continuation_token: str | None,
                   outcome: ToolOutcome) -> ToolGatewayPostResponse:
        self.post_outcome = outcome
        return ToolGatewayPostResponse(action=self._post_action, result=self._post_result)


class _RecordingLlm(LlmGatewayMiddleware):
    def __init__(self, pre_action: str = "next", response: LlmResponse | None = None,
                 mutate_request: LlmContext | None = None,
                 post_action: str = "next", post_response: LlmResponse | None = None) -> None:
        self._pre_action = pre_action
        self._response = response
        self._mutate_request = mutate_request
        self._post_action = post_action
        self._post_response = post_response
        self.pre_ctx: LlmContext | None = None
        self.post_response_in: LlmResponse | None = None

    async def pre(self, context: LlmContext, call_id: str) -> LlmGatewayPreResponse:
        self.pre_ctx = context
        return LlmGatewayPreResponse(action=self._pre_action, response=self._response, request=self._mutate_request)

    async def post(self, call_id: str, continuation_token: str | None,
                   response: LlmResponse) -> LlmGatewayPostResponse:
        self.post_response_in = response
        return LlmGatewayPostResponse(action=self._post_action, response=self._post_response)


def _client(handlers: dict) -> TestClient:
    return TestClient(Host("ext-test", "0.1.0", handlers, target_api_version="0.30").fastapi)


def test_discovery_advertises_each_handler_seam():
    client = _client({"in": _RecordingInput(), "out": _ShortCircuitOutput()})
    body = client.get("/v1/handlers").json()

    assert body["extensionId"] == "ext-test"
    assert body["targetApiVersion"] == "0.30"
    advertised = {h["id"]: h for h in body["handlers"]}
    assert advertised["in"]["seam"] == "agentInput"
    assert advertised["out"]["seam"] == "agentOutput"
    assert advertised["in"]["preEndpoint"] == "/handlers/in/pre"
    assert advertised["in"]["postEndpoint"] == "/handlers/in/post"


def test_input_pre_builds_context_and_echoes_response():
    inp = _RecordingInput()
    client = _client({"in": inp})

    resp = client.post("/handlers/in/pre", json={
        "callId": "c1",
        "context": {"agentId": "a1", "runId": "r1", "nodeId": "n1", "message": "hello"},
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "mutate"
    assert data["continuationToken"] == "ct-1"
    assert data["contextPatch"] == {"k": "v"}

    assert isinstance(inp.pre_ctx, AgentInputContext)
    assert (inp.pre_ctx.agent_id, inp.pre_ctx.run_id, inp.pre_ctx.node_id, inp.pre_ctx.message) == \
        ("a1", "r1", "n1", "hello")
    assert inp.pre_call_id == "c1"


def test_input_post_dispatches_to_handler():
    inp = _RecordingInput()
    client = _client({"in": inp})

    resp = client.post("/handlers/in/post", json={"callId": "c1", "continuationToken": "ct-1"})

    assert resp.status_code == 200
    assert resp.json()["action"] == "mutate"
    assert inp.post_args == ("c1", "ct-1")


def test_output_pre_builds_output_context_and_short_circuits():
    out = _ShortCircuitOutput()
    client = _client({"out": out})

    resp = client.post("/handlers/out/pre", json={
        "callId": "c2",
        "context": {"agentId": "a2", "runId": "r2", "sessionId": "s2",
                    "outputTokens": 5, "inputTokens": 3},
    })

    assert resp.status_code == 200
    assert resp.json()["action"] == "shortCircuit"
    assert isinstance(out.pre_ctx, AgentOutputContext)
    assert (out.pre_ctx.agent_id, out.pre_ctx.session_id,
            out.pre_ctx.output_tokens, out.pre_ctx.input_tokens) == ("a2", "s2", 5, 3)


def test_discovery_advertises_tool_seam():
    body = _client({"gov": _RecordingTool()}).get("/v1/handlers").json()
    advertised = {h["id"]: h for h in body["handlers"]}
    assert advertised["gov"]["seam"] == "toolGatewayMiddleware"


def test_tool_pre_builds_context_and_short_circuits():
    tool = _RecordingTool(pre_action="shortCircuit", pre_error="denied-by-policy")
    client = _client({"gov": tool})

    resp = client.post("/handlers/gov/pre", json={
        "callId": "c1",
        "context": {
            "toolName": "shell", "callId": "c1", "arguments": {"cmd": "ls"},
            "agentId": "a1", "runId": "r1", "privilegeLevel": "Standard",
            "workspaceId": "w1", "allowedTools": ["shell", "edit"],
        },
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "shortCircuit"
    assert data["error"] == "denied-by-policy"

    assert isinstance(tool.pre_ctx, ToolGatewayContext)
    assert tool.pre_ctx.tool_name == "shell"
    assert tool.pre_ctx.arguments == {"cmd": "ls"}
    assert tool.pre_ctx.agent_id == "a1"
    assert tool.pre_ctx.privilege_level == "Standard"
    assert tool.pre_ctx.allowed_tools == ["shell", "edit"]


def test_tool_post_receives_outcome_and_mutates():
    tool = _RecordingTool(post_action="mutate", post_result="[redacted]")
    client = _client({"gov": tool})

    resp = client.post("/handlers/gov/post", json={
        "callId": "c1", "continuationToken": "ct",
        "outcomeResult": "secret", "outcomeError": None,
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "mutate"
    assert data["result"] == "[redacted]"

    assert isinstance(tool.post_outcome, ToolOutcome)
    assert tool.post_outcome.result == "secret"


def test_discovery_advertises_llm_seam():
    body = _client({"gw": _RecordingLlm()}).get("/v1/handlers").json()
    advertised = {h["id"]: h for h in body["handlers"]}
    assert advertised["gw"]["seam"] == "llmGatewayMiddleware"


def test_llm_pre_builds_context_and_short_circuits():
    llm = _RecordingLlm(
        pre_action="shortCircuit",
        response=LlmResponse(text="synthetic", prompt_tokens=1, completion_tokens=2),
    )
    client = _client({"gw": llm})

    resp = client.post("/handlers/gw/pre", json={
        "callId": "c1",
        "request": {
            "messages": [{"role": "user", "content": "hi"}],
            "systemPrompt": "sys",
            "temperature": 0.5,
            "maxTokens": 100,
            "tools": [{"name": "search", "description": "d", "parametersSchema": {"type": "object"}}],
            "responseFormat": {"schema": {"type": "object"}, "name": "R", "strict": True},
            "agentId": "a1",
            "runId": "r1",
        },
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "shortCircuit"
    assert data["response"]["text"] == "synthetic"
    assert data["response"]["promptTokens"] == 1
    assert data["response"]["completionTokens"] == 2

    assert isinstance(llm.pre_ctx, LlmContext)
    assert llm.pre_ctx.messages[0].role == "user"
    assert llm.pre_ctx.messages[0].content == "hi"
    assert llm.pre_ctx.system_prompt == "sys"
    assert llm.pre_ctx.temperature == 0.5
    assert llm.pre_ctx.max_tokens == 100
    assert llm.pre_ctx.tools[0].name == "search"
    assert llm.pre_ctx.response_format.name == "R"
    assert llm.pre_ctx.agent_id == "a1"


def test_llm_pre_mutate_request_echoes_back():
    mutated = LlmContext(messages=[LlmMessage(role="user", content="rewritten")], temperature=0.1, agent_id="a1")
    llm = _RecordingLlm(pre_action="mutate", mutate_request=mutated)
    client = _client({"gw": llm})

    resp = client.post("/handlers/gw/pre", json={
        "callId": "c1",
        "request": {"messages": [{"role": "user", "content": "orig"}]},
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "mutate"
    assert data["request"]["messages"][0]["content"] == "rewritten"
    assert data["request"]["temperature"] == 0.1


def test_llm_post_receives_response_and_mutates():
    llm = _RecordingLlm(post_action="mutate", post_response=LlmResponse(text="[redacted]"))
    client = _client({"gw": llm})

    resp = client.post("/handlers/gw/post", json={
        "callId": "c1", "continuationToken": "ct",
        "response": {"text": "secret", "promptTokens": 3, "completionTokens": 4},
    })

    assert resp.status_code == 200
    data = resp.json()
    assert data["action"] == "mutate"
    assert data["response"]["text"] == "[redacted]"

    assert isinstance(llm.post_response_in, LlmResponse)
    assert llm.post_response_in.text == "secret"
    assert llm.post_response_in.prompt_tokens == 3
