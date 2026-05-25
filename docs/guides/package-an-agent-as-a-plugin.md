# Guide: package an agent as a plugin

End-to-end walkthrough from a blank class library to a plugin that the runtime loads at startup and routes manifests to. Companion to [author-an-agent-in-yaml](author-an-agent-in-yaml.md) — when the declarative path isn't enough and you need C# for custom loops, proprietary providers, or deterministic fallbacks.

Prereqs: a running `vais-agents-runtime` container ([install-the-runtime-locally](install-the-runtime-locally.md)), the `vais` CLI ([install-the-cli](../devops/install-the-cli.md)), .NET 10 SDK.

## 1. Scaffold a class library

```bash
mkdir -p ~/work/MyApp.WeatherAgent
cd ~/work/MyApp.WeatherAgent
dotnet new classlib -n MyApp.WeatherAgent
cd MyApp.WeatherAgent
```

Edit the csproj to reference Vais.Agents packages + publish the plugin with its transitive deps:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
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

`CopyLocalLockFileAssemblies=true` + `SelfContained=false` tells `dotnet publish` to copy transitive deps alongside your primary DLL — the runtime's `AssemblyDependencyResolver` picks them up per-plugin.

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

That's the full contract. The constructor can take any service registered in the host DI container (`IHttpClientFactory`, `ISecretResolver`, `ILogger<WeatherAgent>`, …) — `ActivatorUtilities.CreateInstance` resolves them per-activation. See the [runtime-plugins concept §Plugin authoring](../concepts/runtime-plugins.md#plugin-authoring) for the `IAgentHandlerFactory` shape if you need per-manifest configuration.

## 3. Declare the assembly as a plugin

Add `AssemblyInfo.cs`:

```csharp
using Vais.Agents;

[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent")]
```

- `targetApiVersion: "0.18"` — must major.minor-match the runtime's `VaisRuntimeAbi.CurrentVersion`. Mismatches log `urn:vais-agents:plugin-abi-mismatch` + skip the plugin.
- Second argument lists every `AgentManifest.Handler.TypeName` this plugin exports. Case-sensitive (ordinal).

Build to verify:

```bash
dotnet build -c Release
# → bin/Release/net10.0/MyApp.WeatherAgent.dll
```

## 4. Publish with transitive deps

```bash
dotnet publish -c Release -o ./publish
ls publish/
# MyApp.WeatherAgent.dll
# MyApp.WeatherAgent.deps.json
# MyApp.WeatherAgent.pdb
# (+ any transitive deps not already in the runtime's shared-types carve-out)
```

The shared carve-out already covers `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, MEAI, Polly — those won't land in `publish/` because the build skips them at the publish step (the runtime's versions win).

## 5. Layer onto the runtime image

Two shapes — pick one.

### Shape A — overlay Dockerfile (production)

```dockerfile
# Dockerfile.overlay
FROM ghcr.io/vais-agents/runtime:0.18.0-preview
COPY ./publish /var/lib/vais/plugins/weather-agent
```

```bash
docker build -f Dockerfile.overlay -t my-runtime:0.18.0-preview .
docker run --rm -p 8080:8080 my-runtime:0.18.0-preview
```

Ship the `my-runtime:0.18.0-preview` image through your regular CI + registry + rollout pipeline. Plugin updates = new image tag = standard rolling deploy.

### Shape B — bind mount (local dev)

```bash
docker run --rm -p 8080:8080 \
  -v "$(pwd)/publish:/var/lib/vais/plugins/weather-agent:ro" \
  ghcr.io/vais-agents/runtime:0.18.0-preview
```

Faster iteration — rebuild the plugin, restart the container. Don't use bind mounts in production.

## 6. Verify the plugin loaded

Look for the startup log line:

```
Plugin loading complete — 1 plugin(s) loaded, 1 handler(s) registered.
Loaded plugin 'weather-agent' (targetApiVersion=0.18, handlers=[MyApp.WeatherAgent])
```

Missing log = missing plugin. Common causes:

- **Folder not under `/var/lib/vais/plugins/`.** Each plugin ships in its own subfolder; the loader ignores loose DLLs at the root.
- **Subfolder name ≠ DLL name.** Loader convention: `<folder>/<folder>.dll`. `/var/lib/vais/plugins/weather-agent/MyApp.WeatherAgent.dll` would be skipped; rename the subfolder to `MyApp.WeatherAgent` or accept the `weather-agent` folder name and rename the DLL to match.
- **ABI mismatch.** Check the `targetApiVersion` against the runtime's ABI (logged at startup).
- **`VAIS_PLUGINS_DIRECTORY=""` env set.** Explicit empty string disables the loader. Unset it or point to the real path.

## 7. Apply a manifest

Save `weather.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: weather
  version: "1.0"
spec:
  handler:
    typeName: MyApp.WeatherAgent   # matches VaisPlugin's exported handler
  protocols:
    - kind: Http
  tools: []
```

No `model:` block — the plugin owns execution. Apply:

```bash
vais apply -f weather.yaml
# ✓ weather:1.0 applied (created)
```

If you accidentally include a `model:` block alongside the plugin handler, the apply response carries a `handler-and-declarative-fields-both-set` warning URN. The plugin still wins; the declarative fields are silently ignored at runtime. Remove them to clean the warning.

## 8. Invoke

```bash
vais invoke weather --text "hello"
# Sunny!
```

Behind the scenes: `POST /v1/agents/weather/invoke` → `AgentLifecycleManager.InvokeAsync` → grain activation → `AgentManifestTranslator.TranslateAsync` → `IPluginHandlerRegistry.TryGet("MyApp.WeatherAgent")` → factory returns the plugin's `IAiAgent` → `AiAgentGrain` uses it verbatim → `AskAsync("hello")` returns `"Sunny!"`.

## 9. Update the plugin

Changed the agent's behaviour? You have two options:

**Standard deploy (v0.18+).** Rebuild the plugin, rebuild (or re-push) the runtime image, cycle the pod. Operators treat this like any other deploy.

**Hot reload (v0.22+).** Set `VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap` and bind-mount the plugin directory. Copy a new build of the DLL into the mount — the runtime detects the change, loads the new DLL into a fresh collectible `AssemblyLoadContext`, atomically swaps the registry entry, and invalidates the translator cache. No pod restart required. See [concepts/runtime-plugins.md §Hot reload](../concepts/runtime-plugins.md#hot-reload-v022) for the full swap sequence.

## Troubleshooting

- **`501 urn:vais-agents:handler-not-loaded`** on invoke — plugin didn't register, or the manifest's `handler.typeName` doesn't match any exported handler. Check startup logs for the plugin-load summary and the registered handler list.
- **`500 urn:vais-agents:plugin-factory-throw`** on invoke — the factory threw during activation. `detail` carries the inner exception message. The factory runs per-activation, so a transient failure clears on the next invoke; a permanent one needs a code fix.
- **Apply response carries `urn:vais-agents:handler-and-declarative-fields-both-set`** — your manifest has both a plugin `typeName` AND a `model:` block. Remove one.
- **`AssemblyLoadContext` locking the DLL** during a bind-mount rebuild on Windows — kill + restart the container. Plugins load into non-collectible contexts that hold the file for the process lifetime.
- **`TypeLoadException` when invoking the plugin** — the plugin published a different version of a shared type (e.g., a preview build of `Vais.Agents.Abstractions`) and the runtime rejected the cross-context reference. Confirm the plugin's NuGet pin matches the runtime's tag.

## Security reminder

Plugins run inside the runtime container with full `IServiceProvider` access. They can read secrets, open outbound connections, mutate the registry. **Only load plugins from trusted sources.** Package via overlay images through your existing trust pipeline; don't scrape untrusted DLLs onto a shared PVC. See [runtime-plugins concept §Security posture](../concepts/runtime-plugins.md#security-posture).

## Related

- [runtime-plugins concept](../concepts/runtime-plugins.md) — loader design + isolation + ABI rules.
- [author-an-agent-in-yaml](author-an-agent-in-yaml.md) — the declarative alternative for pure "model + prompt + tools" shapes.
- [install-the-runtime-locally](install-the-runtime-locally.md) — stand up the runtime container if you haven't yet.
- [runtime-configuration reference §Plugin loader](../reference/runtime-configuration.md#plugin-loader) — env var + appsettings key.
- [samples/PluginAgentWeather](../../samples/PluginAgentWeather/README.md) — working csproj + Dockerfile.overlay + README you can clone.
