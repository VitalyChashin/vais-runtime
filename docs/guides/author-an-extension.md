# Guide: author an extension

Extensions inject behaviour into the runtime at well-defined seams — before every agent input, after every agent output, around every tool call, and more. Unlike plugins, extensions are **separate from the agent's business logic**: one extension can bind to many agents simultaneously based on a scope rule.

This guide covers:

1. [C# in-process extension](#1-c-in-process-extension) — shipped as a NuGet package, loaded into a collectible ALC.
2. [Container extension](#2-container-extension) — any language, HTTP protocol, paired pre/post calls.
3. [Scope, priority, and failure modes](#3-scope-priority-and-failure-modes) — operator-visible knobs.
4. [Hot-seam tradeoffs](#4-hot-seam-tradeoffs) — when `host: container` adds latency you can see.
5. [Conformance testing](#5-conformance-testing) — run the shared suite against your extension.

---

## 1. C# in-process extension

### Boilerplate

Create a class library and reference `Vais.Agents.Abstractions`:

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.*" />
```

Add the `[VaisExtension]` assembly attribute and declare your handlers:

```csharp
using Vais.Agents.Abstractions;

[assembly: VaisExtension(
    TargetApiVersion = "0.30",
    Handlers = new[] { typeof(MyInputHandler), typeof(MyOutputHandler) })]
```

### AgentInputMiddleware

```csharp
public sealed class MyInputHandler : AgentInputMiddleware
{
    private readonly ILogger<MyInputHandler> _log;
    public MyInputHandler(ILogger<MyInputHandler> log) => _log = log;

    public override async Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
    {
        _log.LogInformation("[my-ext] in  agent={agent} msg={msg}", ctx.AgentId, ctx.Message);
        await next(); // call next to continue the chain
    }
}
```

`AgentInputContext` fields available in `pre` position:

| Property | Type | Description |
|---|---|---|
| `AgentId` | `string` | Agent name from the manifest. |
| `RunId` | `string?` | Graph run id; null for standalone invocations. |
| `NodeId` | `string?` | Current graph node; null for non-graph invocations. |
| `Message` | `string` | Raw user message text. |
| `Properties` | `IDictionary<string, object?>` | Mutable bag for cross-handler coordination. |

### AgentOutputMiddleware

Fires once **per LLM call** — including each iteration of a tool-calling loop.

```csharp
public sealed class MyOutputHandler : AgentOutputMiddleware
{
    private readonly ILogger<MyOutputHandler> _log;
    public MyOutputHandler(ILogger<MyOutputHandler> log) => _log = log;

    public override async Task InvokeAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct = default)
    {
        await next(); // let the call complete first
        _log.LogInformation("[my-ext] out agent={agent} tokens={tok}",
            ctx.AgentId, ctx.Usage?.OutputTokens ?? 0);
    }
}
```

### IServiceCollection dependencies

The runtime instantiates handlers via DI. Register services in a static method marked with the `[VaisExtensionServices]` attribute:

```csharp
public static class MyExtensionServices
{
    [VaisExtensionServices]
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<MyStateStore>();
        services.AddHttpClient<MyApiClient>(c => c.BaseAddress = new Uri("https://api.example.com"));
    }
}
```

### Packing and publishing

```bash
dotnet pack -c Release -o ./out
```

The DLL is the shipping artefact. Publish it to a NuGet feed or drop it in the runtime's `plugins/` directory (though the canonical path is `vais apply`).

### Manifesting the extension

```yaml
apiVersion: vais.agents/v1
kind: Extension
metadata:
  name: my-logger
  version: "1.0.0"
spec:
  host: csharp
  handlers:
    - id: log-input
      seam: agentInput
      priority: 900
      failureMode: log
    - id: log-output
      seam: agentOutput
      priority: 900
      failureMode: log
```

### Applying it

```bash
vais apply -f my-logger.yaml --dll ./out/MyExtension.dll
```

The runtime loads the DLL into a collectible ALC and binds the handlers to the agent chain on the next activation. To hot-swap a newer version, run `vais apply` again with the updated DLL.

---

## 2. Container extension

Container extensions implement the [Extension Handler Protocol](../../contracts/extensions/handler-protocol.md). Any language that can serve HTTP works.

### FastAPI starter (Python)

```python
from fastapi import FastAPI
from pydantic import BaseModel
from typing import Optional, Any

app = FastAPI()

class AgentInputWire(BaseModel):
    agentId: str
    runId: Optional[str] = None
    nodeId: Optional[str] = None
    message: str

class PreRequest(BaseModel):
    callId: str
    context: AgentInputWire

class PreResponse(BaseModel):
    action: str             # "next" | "shortCircuit" | "mutate"
    continuationToken: Optional[str] = None
    contextPatch: Optional[dict[str, Any]] = None

class PostRequest(BaseModel):
    callId: str
    continuationToken: Optional[str] = None

class PostResponse(BaseModel):
    action: str             # "passThrough" | "mutate"
    contextPatch: Optional[dict[str, Any]] = None

@app.get("/v1/handlers")
def list_handlers():
    return {
        "extensionId": "my-python-ext",
        "version":     "0.1.0",
        "targetApiVersion": "0.30",
        "handlers": [
            {
                "id":           "py-in",
                "seam":         "agentInput",
                "preEndpoint":  "/handlers/py-in/pre",
                "postEndpoint": "/handlers/py-in/post",
            }
        ],
    }

@app.post("/handlers/py-in/pre", response_model=PreResponse)
async def pre(req: PreRequest) -> PreResponse:
    print(f"[my-python-ext] in agent={req.context.agentId}")
    return PreResponse(action="next")

@app.post("/handlers/py-in/post", response_model=PostResponse)
async def post(req: PostRequest) -> PostResponse:
    return PostResponse(action="passThrough")
```

### Dockerfile

```dockerfile
FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY . .
EXPOSE 8080
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
```

### Manifest

```yaml
apiVersion: vais.agents/v1
kind: Extension
metadata:
  name: my-python-ext
  version: "0.1.0"
spec:
  host: container
  image: ghcr.io/your-org/my-python-ext:0.1.0
  port: 8080
  handlers:
    - id: py-in
      seam: agentInput
      priority: 800
      failureMode: log
```

### Applying it

```bash
vais apply -f my-python-ext.yaml
```

The runtime pulls the image, starts the container, calls `GET /v1/handlers` to discover handlers, and registers them. The container runs for the lifetime of the extension.

### Mutation semantics

To modify the agent's context from the pre-handler, return `action: "mutate"` with a `contextPatch` object. The runtime merges the patch into `AgentInputContext.Properties`:

```python
@app.post("/handlers/py-in/pre", response_model=PreResponse)
async def pre(req: PreRequest) -> PreResponse:
    memories = lookup_memories(req.context.agentId, req.context.message)
    return PreResponse(
        action="mutate",
        contextPatch={"mem0.memories": memories},
    )
```

> **Note:** `AgentInputContext.Properties` is the only mutable surface available via `contextPatch`. `AgentInputContext.Message` is read-only from the container perspective.

### Short-circuit

To stop the chain without invoking the agent (e.g., for a cache hit or an access-control check), return `action: "shortCircuit"`:

```python
@app.post("/handlers/py-in/pre", response_model=PreResponse)
async def pre(req: PreRequest) -> PreResponse:
    if is_blocked(req.context.agentId):
        return PreResponse(action="shortCircuit")
    return PreResponse(action="next")
```

---

## 3. Scope, priority, and failure modes

### Scope

Without a `scope` field the extension binds to **all agents cluster-wide**.

```yaml
spec:
  scope:
    agentIds:
      - research-agent
      - summarizer-agent
```

| Field | Description |
|---|---|
| `agentIds` | List of agent names. Must include the agent exactly; no glob. |
| `workspaces` | Agents in these workspaces only. |
| `selector` | Label selector, same syntax as Kubernetes. |

All specified fields AND together; values within a field OR together.

### Priority

Lower numeric value runs first. The full ordering per agent:

```
static DI middleware (registered in AddVaisRuntime) → extension handlers (ascending priority)
```

Priority must be unique per seam per agent scope. A second `vais apply` that would create a collision returns HTTP 409 with both extension names and the conflicting priority.

Good defaults: `100..500` for infrastructure extensions; `500..900` for observability.

### Failure modes

| Mode | Behaviour on handler error |
|---|---|
| `fail` | Abort the agent turn with an error. Use for access-control or required enrichment. |
| `log` | Log a warning and continue the chain as if the handler didn't exist. |
| `skip` | Silently continue (for best-effort telemetry / decorators). |

---

## 4. Hot-seam tradeoffs

`host: container` adds a synchronous HTTP round-trip to every handler invocation. On hot seams — `agentInput` and `agentOutput`, which fire on every LLM call — a slow container adds directly to per-turn latency.

The runtime enforces an opt-in for hot-seam container extensions:

```bash
vais apply -f my-ext.yaml --accept-latency-cost
```

Without the flag the server returns HTTP 412 with a breakdown of affected handlers and current per-turn overhead.

**Guidelines:**

- Use `host: csharp` for latency-sensitive handlers (logging, basic enrichment).
- Use `host: container` for compute-heavy handlers where isolation matters (ML inference, external API calls with complex auth).
- For the output seam specifically, `post` runs after the agent has already responded; a slow `post` does not block the user-visible response — only the next turn.

---

## 5. Conformance testing

The `Vais.Agents.Runtime.Extensions.Conformance` test project ships with the runtime and verifies that any extension host satisfies the same seam semantics.

### Running the suite against your C# extension

```bash
dotnet test tests/Vais.Agents.Runtime.Extensions.Conformance \
  --filter "CsharpExtension"
```

### Plugging in your own fixture

Derive from `ExtensionConformanceBase` and implement `CreateDescriptorAsync`:

```csharp
public sealed class MyExtensionConformanceTests : ExtensionConformanceBase
{
    protected override Task<ExtensionDescriptor> CreateDescriptorAsync(
        string extensionId, ExtensionScope? scope, int priority)
    {
        // Return a descriptor whose agentInput handler
        // is backed by your extension's host.
        return Task.FromResult(new ExtensionDescriptor(
            ExtensionId: extensionId,
            Version:     "1.0.0",
            Manifest:    MakeManifest(extensionId, "1.0.0", scope),
            Handlers:    new[] { /* your HandlerBinding */ },
            LoadContext: null));
    }
}
```

The base class provides 6 tests covering registration, scope, priority ordering, remove, and swap. All tests must pass before an extension host is considered conformant.
