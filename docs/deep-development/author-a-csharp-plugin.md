# Author a C# plugin

You'll author a C# `IAiAgent` implementation, publish it as a class-library DLL, and load it into the runtime as an in-process plugin. End state: `vais apply` against an agent manifest with `handler.typeName: MyApp.WeatherAgent.WeatherAgent` routes invocations into your C# code; `vais invoke` returns whatever your agent returns.

## When this path?

C# in-process is the right plugin model when:

- You want the lowest-latency option — no IPC, no subprocess, no container.
- Your agent code can run inside the runtime's trust boundary (it sees the full DI container, including secrets).
- You're comfortable with .NET tooling and want direct access to MEAI, SK, MAF, or any other .NET library.

For polyglot (Python, Go, anything else) or stricter isolation (container, network policy), see **[Build a LangGraph plugin](build-a-langgraph-plugin.md)** or **[Author a container plugin in Go](author-a-container-plugin-in-go.md)**.

## Prerequisites

- A running `vais-agents-runtime` ([DevOps section](../devops/index.md)).
- The `vais` CLI installed and pointed at the runtime.
- .NET 9 SDK.
- Access to the `Vais.Agents.*` NuGet packages. These are not on nuget.org. Add a `NuGet.config` next to your csproj that points at the private feed (or the local `agentic/artifacts/packages` folder from your checkout):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="vais-local" value="path/to/agentic/artifacts/packages" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="vais-local">
      <package pattern="Vais.Agents.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## 1. Scaffold a class library

```bash
mkdir -p ~/work/MyApp.WeatherAgent
cd ~/work/MyApp.WeatherAgent
dotnet new classlib -n MyApp.WeatherAgent
cd MyApp.WeatherAgent
```

Edit the csproj to reference Vais.Agents packages and publish transitive deps:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
    <AssemblyName>MyApp.WeatherAgent</AssemblyName>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Vais.Agents.Abstractions" Version="0.18.0-preview" />
    <PackageReference Include="Vais.Agents.Core" Version="0.18.0-preview" />
  </ItemGroup>
</Project>
```

`CopyLocalLockFileAssemblies=true` + `SelfContained=false` tells `dotnet publish` to copy transitive deps alongside the primary DLL — the runtime's `AssemblyDependencyResolver` picks them up per-plugin.

## 2. Write the agent

Delete `Class1.cs`. Add `WeatherAgent.cs`:

```csharp
using Vais.Agents;
using Vais.Agents.Core;

namespace MyApp.WeatherAgent;

public sealed class WeatherAgent : IAiAgent
{
    private readonly InMemoryAgentSession _session = new(agentId: "weather", sessionId: "default");

    public string? SystemPrompt { get; set; }
    public IAgentSession Session => _session;
    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), ct);
        var reply = "Sunny!";
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, reply), ct);
        return reply;
    }

    public void Reset() => _session.ResetAsync().AsTask().GetAwaiter().GetResult();
}
```

That's the full contract. The constructor can take any service registered in the host DI container (`IHttpClientFactory`, `ISecretResolver`, `ILogger<WeatherAgent>`, …) — `ActivatorUtilities.CreateInstance` resolves them per-activation.

## 3. Declare the assembly as a plugin

Add `AssemblyInfo.cs`:

```csharp
using Vais.Agents;

[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent.WeatherAgent")]
```

- `targetApiVersion` — must major.minor-match the runtime's `VaisRuntimeAbi.CurrentVersion` (currently `"0.18"`). Mismatches log `urn:vais-agents:plugin-abi-mismatch` and skip the plugin.
- Second argument lists every `AgentManifest.Handler.TypeName` this plugin exports. Must be the **full CLR type name** (namespace + class, e.g. `"MyApp.WeatherAgent.WeatherAgent"`). Case-sensitive.

Build to verify:

```bash
dotnet build -c Release
# → bin/Release/net9.0/MyApp.WeatherAgent.dll
```

## 4. Publish with transitive deps

```bash
dotnet publish -c Release -o ./publish
ls publish/
# MyApp.WeatherAgent.dll
# MyApp.WeatherAgent.deps.json
# Vais.Agents.Abstractions.dll, Vais.Agents.Core.dll, Polly.Core.dll, …
```

The publish output will include `Vais.Agents.Abstractions.dll`, `Vais.Agents.Core.dll`, Polly, and other shared assemblies — `dotnet publish` copies everything the project depends on. This is expected. At runtime the loader's `PluginAssemblyLoadContext` redirects resolution of shared types back to the host's copies, so your copies are ignored in favour of the runtime's versions. You do not need to strip them manually.

## 5. Layer onto the runtime image

Two shapes — pick one.

### Shape A — overlay Dockerfile (production)

```dockerfile
# Dockerfile.overlay
FROM vais-agents-runtime:local
COPY ./publish /var/lib/vais/plugins/MyApp.WeatherAgent
```

```bash
docker build -f Dockerfile.overlay -t my-runtime:1.0 .
docker run --rm -p 8080:8080 my-runtime:1.0
```

Ship `my-runtime:1.0` through your regular CI + registry + rollout. Plugin updates = new image tag = standard rolling deploy.

### Shape B — bind mount (local dev)

```bash
docker run --rm -p 8080:8080 \
  -v "$(pwd)/publish:/var/lib/vais/plugins/MyApp.WeatherAgent:ro" \
  vais-agents-runtime:local
```

Faster iteration — rebuild, restart. Don't use bind mounts in production.

## 6. Verify the plugin loaded

Look for the startup log lines:

```
Loaded plugin 'MyApp.WeatherAgent' (targetApiVersion=0.18, handlers=[MyApp.WeatherAgent.WeatherAgent])
Plugin loading complete — 1 plugin(s) loaded, 1 handler(s) registered.
```

Missing log = missing plugin. Common causes:

- **Folder not under `/var/lib/vais/plugins/`.** Each plugin ships in its own subfolder; loose DLLs at the root are ignored.
- **Subfolder name ≠ DLL name.** Loader convention: `<folder>/<folder>.dll`. Rename one to match the other.
- **ABI mismatch.** Check `targetApiVersion` against the runtime's ABI (logged at startup).
- **`VAIS_PLUGINS_DIRECTORY=""` env set.** Explicit empty string disables the loader.

## 7. Apply a manifest and invoke

```yaml
# weather.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: weather
  version: "1.0"
spec:
  handler:
    typeName: MyApp.WeatherAgent.WeatherAgent
  protocols:
    - kind: Http
  tools: []
```

No `model:` block — the plugin owns execution.

```bash
vais apply -f weather.yaml
vais invoke weather --text "hello"
# → Sunny!
```

The `handler.typeName` value must be the **full CLR type name** and must match exactly what you declared in `[VaisPlugin]`. If they differ, `vais invoke` returns `503 urn:vais-agents:backend-unavailable` — check the startup log for the registered handler list.

If you accidentally include a `model:` block alongside the plugin handler, the apply response carries a `handler-and-declarative-fields-both-set` warning URN. The plugin still wins; the declarative fields are silently ignored. Remove them to clean the warning.

## 8. Update the plugin

**Standard deploy.** Rebuild the plugin, rebuild (or re-push) the runtime image, cycle the pod. Operators treat it like any deploy.

**Hot reload.** Set `VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap` and bind-mount the plugin directory. Copy a new build of the DLL into the mount — the runtime detects the change, loads the new DLL into a fresh collectible `AssemblyLoadContext`, atomically swaps the registry entry, and invalidates the translator cache. No pod restart. See [`concepts/runtime-plugins.md`](../concepts/runtime-plugins.md#hot-reload) for the swap sequence.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `503 urn:vais-agents:backend-unavailable` | Plugin didn't register, or manifest `handler.typeName` doesn't match the registered key | Check startup logs for plugin-load summary + registered handlers; confirm `[VaisPlugin]` handler name and manifest `typeName` are the same full CLR name |
| `500 urn:vais-agents:plugin-factory-throw` | Factory threw during activation | `detail` carries the inner exception; transient = retries clear, permanent = code fix |
| `AssemblyLoadContext` locks DLL during bind-mount rebuild (Windows) | Plugin in non-collectible context holding the file | Kill + restart the container |
| `TypeLoadException` on invoke | Plugin shipped a different version of a shared type | Confirm plugin's NuGet pin matches runtime's |

## Security reminder

C# plugins run inside the runtime container with full `IServiceProvider` access. They can read secrets, open outbound connections, mutate the registry. **Only load plugins from trusted sources.** Package via overlay images through your existing trust pipeline; don't scrape untrusted DLLs onto a shared volume. For untrusted code, use a [container plugin](author-a-container-plugin-in-go.md) — those run in their own sandboxed Docker container with `NetworkPolicy` and `securityContext` defaults.

## What you built

- A C# class library that implements `IAiAgent`.
- An overlay-image deployment path with predictable, CI-friendly rollouts.
- A manifest routing `vais apply` / `vais invoke` to your code through the standard control plane.

## Next

- **[Build a LangGraph plugin](build-a-langgraph-plugin.md)** — same durability contract, Python instead of C#.
- **[Author a container plugin in Go](author-a-container-plugin-in-go.md)** — language-neutral via the IP-1 HTTP protocol.
- [Concepts → Runtime plugins](../concepts/runtime-plugins.md) — loader design, isolation, ABI rules, hot-reload semantics.
- [Full C# plugin guide](../guides/package-an-agent-as-a-plugin.md) — depth: shared-types carve-out, `IAgentHandlerFactory`, per-activation DI patterns.
- [Sample → `samples/PluginAgentWeather`](../../samples/PluginAgentWeather/) — working csproj + Dockerfile.overlay you can clone.
