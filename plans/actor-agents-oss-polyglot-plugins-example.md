# Polyglot plugins — end-to-end example: research-planner

End-to-end reference example anchoring the v0.23 Python-plugins pillar. Describes the canonical shape of a Python plugin plus the declarative agent that consumes it. This doc is the **test-reference specification** — the sample plugin ships under `samples/PluginAgentResearchPlanner/`, and integration tests assert the observable behaviour below.

Created 2026-04-24. **Status**: locked (test reference for v0.23).

---

## Scenario

A research assistant agent. Given a research question, it produces a structured report by:
1. Decomposing the question into sub-questions (Python).
2. Scoring how completely a current plan addresses the question (Python).
3. Searching the web per sub-question (external MCP — Tavily).
4. Summarising findings into a report (Python).

Planning logic (1)-(2), (4) is deterministic LLM-assisted work that benefits from Python's Pydantic + prompt-template ecosystem. Web search is a separate external MCP service. The LLM orchestration loop (which tool next, history, streaming) is the .NET runtime.

**User mental model:** "I authored an agent in Python + YAML."
**Architectural truth:** declarative .NET agent with Python-contributed tools. The scope narrowing (tool-level, not agent-level) is invisible to the author in this shape.

---

## Plugin directory layout

```
research-planner/
├── plugin.yaml                     # runtime descriptor (language-agnostic)
├── pyproject.toml                  # Python metadata + [tool.vais.plugin]
├── uv.lock                         # deterministic, hash-pinned
├── Dockerfile.overlay              # packages plugin into runtime image
├── .venv/                          # pre-baked by `uv venv --relocatable`
│   ├── bin/python                  # relative-path shebang
│   └── lib/python3.13/site-packages/
└── src/research_planner/
    ├── __init__.py
    ├── server.py                   # MCP entrypoint
    ├── planner.py                  # planning logic
    └── schemas.py                  # Pydantic tool arg/return models
```

## plugin.yaml — runtime descriptor

Language-agnostic. A Node or Go plugin would differ only in `runtime:` and `interpreter:`.

```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: research-planner
spec:
  runtime: python
  entrypoint: src/research_planner/server.py
  python:
    version: "3.13"
    interpreter: .venv/bin/python     # resolved relative to plugin dir
  health:
    handshakeTimeoutSeconds: 5        # MCP initialize handshake budget
    restartPolicy: exponentialBackoff # on crash
```

## pyproject.toml — Python-side metadata

```toml
[project]
name = "research-planner"
version = "0.1.0"
requires-python = ">=3.13"
dependencies = [
  "mcp >=1.27,<2",
  "pydantic >=2.8,<3",
]

[tool.vais.plugin]
targetApiVersion = "0.23"
tools = ["decompose_task", "score_plan_completeness", "summarize_findings"]

[project.entry-points."vais.plugins"]
server = "research_planner.server:main"

[tool.uv]
dev-dependencies = ["ruff", "pytest"]
```

## src/research_planner/schemas.py — tool contracts

```python
from pydantic import BaseModel, Field

class DecomposeTaskArgs(BaseModel):
    question: str = Field(description="The research question to decompose.")
    max_subquestions: int = Field(default=5, ge=1, le=10)

class ScoredPlan(BaseModel):
    coverage_score: float = Field(ge=0.0, le=1.0)
    missing_angles: list[str]
    rationale: str

class SummarizeFindingsArgs(BaseModel):
    question: str
    findings: list[str] = Field(min_length=1)
    max_length_chars: int = Field(default=2000, ge=200)
```

## src/research_planner/server.py — MCP entrypoint

The whole plugin. Framework-specific boilerplate is only the MCP SDK decorators.

```python
"""Research-planner MCP server. Spawned by the Vais runtime over stdio."""
from mcp.server.fastmcp import FastMCP
from .planner import Planner
from .schemas import DecomposeTaskArgs, ScoredPlan, SummarizeFindingsArgs

mcp = FastMCP("research-planner")
_planner = Planner()

@mcp.tool()
def decompose_task(args: DecomposeTaskArgs) -> list[str]:
    """Break a research question into answerable sub-questions."""
    return _planner.decompose(args.question, args.max_subquestions)

@mcp.tool()
def score_plan_completeness(
    question: str, subquestions: list[str]
) -> ScoredPlan:
    """Judge whether the sub-question list covers the original question."""
    return _planner.score(question, subquestions)

@mcp.tool()
def summarize_findings(args: SummarizeFindingsArgs) -> str:
    """Reduce a list of findings to a single coherent summary."""
    return _planner.summarize(args.question, args.findings, args.max_length_chars)

def main() -> None:
    mcp.run(transport="stdio")

if __name__ == "__main__":
    main()
```

`planner.py` holds the actual logic (prompt templates, Pydantic validation, whatever — opaque to the runtime).

## Build-time author commands

```bash
cd research-planner
uv sync --frozen                 # create .venv from uv.lock
uv venv --relocatable            # rewrite shebangs to relative paths
```

The `.venv/` is now portable across image mount points.

## Dockerfile.overlay — shipping it

```dockerfile
FROM ghcr.io/vais-agents/runtime:0.23.0-preview
COPY research-planner /var/lib/vais/plugins/research-planner
```

Same shape as the v0.18 .NET plugin overlay.

---

## Declarative agent manifest using the plugin

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: research-agent
  version: "1.0"
spec:
  model:
    provider: anthropic
    id: claude-haiku-4-5
  systemPromptSpec:
    text: |
      You are a research assistant. Given a question, plan your research
      by decomposing it into sub-questions, gather evidence via web search,
      and produce a structured report.

      Use tools in this order:
        1. decompose_task            — break the question into sub-questions.
        2. score_plan_completeness   — validate the plan.
        3. tavily_search             — gather evidence (one call per sub-question).
        4. summarize_findings        — produce the final report.
  tools:
    - name: decompose_task
      source: mcp:research-planner
    - name: score_plan_completeness
      source: mcp:research-planner
    - name: summarize_findings
      source: mcp:research-planner
    - name: tavily_search
      source: mcp:tavily
  mcpServers:
    - name: tavily
      transport: http
      url: https://mcp.tavily.com/
      auth:
        kind: bearer
        tokenRef: secret://env/TAVILY_API_KEY
  protocols:
    - kind: Http
```

Three load-bearing details:

1. `source: mcp:research-planner` reuses the v0.7 MCP source-prefix convention. **No new manifest schema.**
2. `research-planner` is NOT listed under `mcpServers:` — the runtime manages its lifecycle automatically because it's a loaded plugin. Tavily is an external MCP service, so the operator declares it.
3. The `model:` block is .NET-declarative (v0.17 path). The Python plugin contributes tools, not the agent loop.

---

## Runtime lifecycle at silo startup

```
[startup] IPythonPluginHost.StartAsync
  → scan /var/lib/vais/plugins/*/plugin.yaml where runtime: python
  → for research-planner:
      • resolve .venv/bin/python (relative → absolute)
      • Process.Start(
          FileName  = "/var/lib/vais/plugins/research-planner/.venv/bin/python",
          Arguments = "src/research_planner/server.py",
          WorkingDirectory = "/var/lib/vais/plugins/research-planner",
          RedirectStandardInput = true,
          RedirectStandardOutput = true)
      • wire process stdio → IMcpClient (via Vais.Agents.Protocols.Mcp)
      • send MCP initialize; wait for response (5s budget)
      • call tools/list; verify names match [tool.vais.plugin].tools
      • register client as IToolSource "mcp:research-planner"
  → log: "Plugin 'research-planner' loaded (pid=12345, handlers=3, abi=0.23)"
```

The `IToolSource` registration bridges the Python world into the rest of the stack. `AggregatingToolRegistry` picks it up; `StatefulAiAgent` sees the tools indistinguishably from .NET-native ones.

## A single invocation, traced

```
$ vais invoke research-agent --text "What's driving the 2026 energy-storage cost decline?"
```

```
1. HTTP POST /v1/agents/research-agent/invoke → AiAgentGrain
2. Grain resolves tool registry (research-planner tools + tavily + .NET-native)
3. Loop iteration 1 — LLM sees tools, emits:
      tool_call { name="decompose_task",
                  args={"question":"What's driving...", "max_subquestions":5} }
   → IToolCallDispatcher.DispatchAsync
   → McpBackedTool.InvokeAsync
   → stdio JSON-RPC → research-planner subprocess
   → returns: ["cost of lithium...", "gigafactory capacity...",
               "policy subsidies...", "recycling advances...", "grid demand..."]
4. Loop iteration 2 — LLM calls score_plan_completeness (same path).
5. Loop iterations 3-7 — LLM calls tavily_search per sub-question.
6. Loop iteration 8 — LLM calls summarize_findings.
7. LLM emits final assistant turn; returned via SSE stream.
```

From the user's CLI, the round trip reads as a single streaming response. Under the hood the planning tools are JSON-RPC calls to a Python subprocess; the search tools are JSON-RPC calls to a remote MCP service; the LLM loop and history are .NET. None of this leaks into the author's mental model — they wrote Python + YAML.

---

## What this example exercises (→ test coverage expectations)

Each of these becomes an assertion in the v0.23 integration test suite.

| Exercised capability | Findings Q | Test target |
|---|---|---|
| Tool-level Python plugin contributes tools | Q2 + Q4 | `McpBackedTool.InvokeAsync` round-trip through Python subprocess |
| Silo-scoped subprocess lifecycle | Q5 | `IPythonPluginHost` spawns on startup; `tools/list` succeeds |
| Pre-baked uv venv in OCI layer | Q3 | Sample ships with `.venv/` baked; loads without network |
| Two-level descriptor parsing | Q7 | `plugin.yaml` + `[tool.vais.plugin]` both read; ABI mismatch logged |
| Reuse of `McpToolSource` / `McpBackedTool` / `IToolSource` | Q4 | No new abstraction introduced — Python tools indistinguishable from .NET tools at the registry level |
| Declarative manifest referencing Python tools via `mcp:` prefix | Q9 | `source: mcp:research-planner` resolves without manifest-schema change |
| Subprocess restart on crash | Q5 | Kill Python process mid-session; next tool call succeeds after backoff |
| ABI mismatch rejection | Q7 | Set `targetApiVersion = "99.99"` in pyproject; plugin load fails with `urn:vais-agents:python-plugin-abi-mismatch` |
| Tool-list mismatch detection | Q7 | `[tool.vais.plugin].tools` lists a tool the server doesn't expose; WARN logged |

## What this example does NOT exercise (→ out of scope for v0.23 tests)

- Python implementing the full agent loop — deferred; A2A covers it.
- Token-by-token streaming from a Python tool — out of scope (Q10 deferral).
- Untrusted/sandboxed execution — Track B (separate spike).
- Non-Python plugin runtimes — deferred to follow-up pillars.

---

## Integration test blueprint (for v0.23 pillar PRs)

**Golden-path test** (must pass in PR 3 acceptance):
1. Runtime starts with the bundled `research-planner` sample at `/var/lib/vais/plugins/research-planner/`.
2. `IPythonPluginHost.LoadedPlugins` contains one entry after `StartAsync`.
3. `IToolRegistry.Tools` contains three tools with source prefix `mcp:research-planner`.
4. `vais apply -f research-agent.yaml` succeeds.
5. `vais invoke research-agent --text "test"` produces a response (mocked LLM that forces a specific tool call order).
6. Each tool call traverses the stdio boundary (assert via `IToolCallDispatcher` events).

**Failure-mode tests** (must pass in PR 3 acceptance):
1. Plugin directory with missing `.venv` → `urn:vais-agents:python-plugin-load-failed` at startup; runtime continues (other plugins still load).
2. `targetApiVersion = "99.99"` → `urn:vais-agents:python-plugin-abi-mismatch`; plugin skipped.
3. Plugin subprocess exits mid-invocation → tool call returns `urn:vais-agents:python-plugin-unavailable` after restart-with-backoff exhausts.
4. `tools/list` response doesn't include a declared tool → WARN log; declared-but-missing tools silently dropped.
5. MCP initialize handshake exceeds `handshakeTimeoutSeconds: 5` → `urn:vais-agents:python-plugin-handshake-timeout`; subprocess killed; plugin skipped.

**Open-question integration tests** (shape depends on pillar-plan decisions):
- Secret propagation — deferred to pillar plan decision; test added once the mechanism is chosen.
- Log correlation — deferred to pillar plan decision.

---

## Related

- [polyglot-plugins-findings](actor-agents-oss-polyglot-plugins-findings.md) — decisions anchoring this example.
- [polyglot-plugins-spike](actor-agents-oss-polyglot-plugins-spike.md) — research scope that informed the findings.
- [v0.18 plugin model findings](actor-agents-oss-v0.18-plugin-model-findings.md) — .NET plugin baseline this extends.
- [v0.22 plugin hot-reload pillar](actor-agents-oss-v0.22-plugin-hot-reload-pillar.md) — hot-reload infrastructure; Python equivalent is pillar-plan scope.
