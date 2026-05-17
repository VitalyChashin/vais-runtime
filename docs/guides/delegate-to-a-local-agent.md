# Guide: delegate to a local agent

Wire a sub-agent running in the same runtime as a first-class tool so a coordinator agent can delegate work in-process without an HTTP round-trip. This is the P7 default pattern: "agent-as-tool over peer A2A."

## When to use each pattern

| Situation | Pattern |
|-----------|---------|
| Sub-agent lives in the same process / Orleans cluster | `agent:<name>` source (this guide) |
| Sub-agent is a separate deployment or owned by another team | `a2a:<url>` source — see [delegate to an A2A remote agent](delegate-to-a2a-remote-agent.md) |
| You want to re-home the conversation rather than get a result back | `handoffs[]` — see [author an agent in YAML](author-an-agent-in-yaml.md) |

## Packages

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Core" Version="0.15.0-preview" />
```

## Declare the sub-agent in the manifest

Add a `localAgents` section and reference it from `tools`:

```yaml
kind: agent
spec:
  id: coordinator
  model:
    provider: openai
    id: gpt-4.1
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt: |
    You are a coordinator. Delegate arithmetic to call_math_specialist.
  localAgents:
    - name: math-specialist         # logical name used in tools[].source
      agentId: math-specialist      # id in IAgentRegistry (defaults to name)
  tools:
    - source: agent:math-specialist
      name: call_math_specialist
      description: Delegate an arithmetic question to the math-specialist sub-agent.
```

The `math-specialist` agent must also be declared (as its own manifest or pre-registered in `IAgentRegistry`):

```yaml
kind: agent
spec:
  id: math-specialist
  model:
    provider: openai
    id: gpt-4.1-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt: You are a math specialist. Answer arithmetic questions directly.
```

## What the runtime does

When `AgentManifestTranslator` resolves the `agent:math-specialist` source:

1. Looks up `math-specialist` in the `localAgents` list.
2. Resolves the target manifest from `IAgentRegistry`.
3. Creates a `LocalAgentTool` pointing at `IAgentRuntime` (resolved lazily to avoid a DI cycle).

At every tool call the coordinator makes:

1. A deterministic session id is derived: `SHA256(runId + toolName + argHash)`.
2. `IAgentRuntime.GetOrCreateForSession(agentId, sessionId)` creates a fresh agent instance.
3. `AgentContext` propagates: `UserId`, `TenantId`, `WorkspaceId` carry through; `MaxChainDepth` decrements by 1.
4. After the call returns, the session is removed so state does not accumulate across calls.

## Wire it in code (without the manifest loader)

```csharp
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

var runtime = new InMemoryAgentRuntime(specialistProvider);

var mathTool = new LocalAgentTool(
    runtimeFactory: () => runtime,        // lazy — avoids DI cycle
    effectiveAgentId: "math-specialist",
    name: "call_math_specialist",
    description: "Delegate arithmetic to the math-specialist sub-agent.",
    allowCallerSuppliedSession: false,
    propagateAllowedTools: true);

var coordinator = new StatefulAiAgent(
    coordinatorProvider,
    new StatefulAgentOptions
    {
        AgentName = "coordinator",
        ToolRegistry = new SimpleRegistry(mathTool),
    });
```

## LocalAgentRef fields

YAML manifest authors set whichever fields they need — all but `name` are optional. The C# record splits these into positional ctor params (`Name`, `AgentId`, `AgentVersion`, `Mode`) and init-only properties (`Description`, `AllowCallerSuppliedSession`, `PropagateAllowedTools`); for hand-rolled C# usage the init-only ones go in an object initializer (`new LocalAgentRef("math") { PropagateAllowedTools = false }`).

| Field | Type | Default | Purpose |
|-------|------|---------|---------|
| `name` | `string` | — | Logical name; matched against `tools[].source = "agent:<name>"`. |
| `agentId` | `string?` | `name` | Agent id in `IAgentRegistry`. Use when the id differs from the name. |
| `agentVersion` | `string?` | latest | Pin to a specific manifest version. |
| `mode` | `Blocking \| Background` | `Blocking` | `Blocking` — waits for the sub-agent reply. `Background` — fire-and-forget (Phase 2). |
| `description` | `string?` | manifest description | Overrides the tool description the model sees. |
| `allowCallerSuppliedSession` | `bool` | `false` | Let the caller pass `sessionId` in tool args to resume a named session. |
| `propagateAllowedTools` | `bool` | `true` | Copy `AgentContext.AllowedTools` into the child context. |

## Depth guard

`AgentContext.MaxChainDepth` controls how deep the delegation chain can go. If the coordinator has depth 3, the specialist gets depth 2. When depth reaches 0, `LocalAgentTool` returns an error string rather than calling the agent, preventing infinite delegation loops.

Set it when creating the top-level context:

```csharp
using var _ = contextAccessor.Push(new AgentContext { MaxChainDepth = 5 });
```

The default is `null` (unlimited) for code that does not set a depth.

## Persistent sessions (allowCallerSuppliedSession)

By default each tool call gets a fresh session that is removed on completion. To give the sub-agent a persistent memory across calls, set `allowCallerSuppliedSession: true` and pass `sessionId` in the tool args:

```json
{"message": "What is 42 × 7?", "sessionId": "my-thread"}
```

The session is NOT removed after the call; the coordinator is responsible for cleaning it up via `IAgentRuntime.RemoveSession`.

## Background delegation (fire-and-forget)

Set `mode: Background` on the `localAgents` entry to get a fire-and-forget sub-run. The tool returns immediately with a JSON handle; the coordinator can poll status via three automatically-injected management tools.

```yaml
localAgents:
  - name: researcher
    agentId: researcher
    mode: Background        # fire-and-forget
tools:
  - source: agent:researcher
    name: start_research
    description: Start a background research task.
```

When the coordinator calls `start_research` it receives:

```json
{"handle": "run-abc__start_research__a1b2c3d4", "status": "pending"}
```

Three management tools are added to the coordinator automatically (once per manifest build):

| Tool | Args | Returns |
|------|------|---------|
| `list_background_agents` | none | Array of run records for the current coordinator run |
| `view_background_agent` | `handle` | Single run record (status, result, error) |
| `cancel_background_agent` | `handle` | `{"handle":"…","cancelled":true\|false}` |

The coordinator can instruct the model to call `view_background_agent` after starting a task, or to poll until `status` is `"Completed"`.

### Wire background delegation in code

```csharp
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

var runtime = new InMemoryAgentRuntime(workerProvider);
var tracker = new InMemoryBackgroundAgentTracker(runtime);

var startResearch = new BackgroundLocalAgentTool(
    runtimeFactory: () => runtime,
    tracker: tracker,
    effectiveAgentId: "researcher",
    name: "start_research",
    description: "Start a background research task.");

// Inject management tools alongside the start tool:
var allTools = new[] { startResearch }
    .Concat(BackgroundAgentManagementTools.Create(tracker))
    .ToList();
```

### P5 scaling contract

| Property | Blocking (`LocalAgentTool`) | Background — Orleans (`OrleansBackgroundAgentTracker`) | Background — InMemory (`InMemoryBackgroundAgentTracker`) |
|---|---|---|---|
| Survives silo restart | Child is a durable grain; parent turn re-executes on restart; child tool calls replay via journal | Run grain persists; reactivates and re-schedules `RunAsync`; child invocation journaled by `RunId = handle` — no LLM re-execution | ❌ dev/test only — lost on process restart |
| Status / cancel from any silo | N/A (synchronous) | ✅ grain calls route to any node in the cluster | ❌ process-local only |
| Cross-silo visibility | N/A | ✅ index grain keyed by `parentRunId` | ❌ process-local only |

Use `InMemoryBackgroundAgentTracker` in development and tests. Deploy `OrleansBackgroundAgentTracker` via the Orleans host for production (it is registered automatically by `UseOrleansAgentRuntime`).

## Things that catch people

- **`runtimeFactory` must be a lambda, not a direct reference.** `IAgentRuntime` depends on `IAgentManifestTranslator` which emits `LocalAgentTool`. Resolving `IAgentRuntime` in the translator constructor would be a DI cycle; the lambda defers resolution to first invocation.
- **Session ids cannot contain `/`.** Orleans grain keys forbid it. `LocalAgentTool` sanitises deterministic ids automatically; caller-supplied ids are sanitised the same way (`/` → `_`).
- **Child agent does not share the coordinator's conversation history.** Each sub-agent has its own `StatefulAiAgent` instance. This is intentional: the sub-task is isolated.
- **Background management tools are injected once per manifest build.** If a coordinator manifest declares two background sub-agents, `AgentManifestTranslator` adds only one copy of each management tool (idempotent by name).

## See also

- [Delegate to an A2A remote agent](delegate-to-a2a-remote-agent.md) — cross-runtime peer delegation
- [Author an agent in YAML](author-an-agent-in-yaml.md) — full manifest authoring reference
- [Expose MCP tools to an agent](expose-mcp-tools-to-an-agent.md) — tool wiring patterns
- Sample: `samples/AgentAsToolDelegation/`
