# Runtime plugins

**v0.18 Pillar C.** The runtime loads code-authored `IAiAgent` implementations from DLLs dropped under `/var/lib/vais/plugins` and routes manifests to them when `AgentHandlerRef.TypeName` matches. Partners ship agents whose behaviour doesn't fit the "model + prompt + tools" declarative shape — custom loops, proprietary providers, deterministic fallbacks — without rebuilding the runtime container.

> **v0.23:** Python tool-contributing plugins are now supported via a separate loader path (`IPythonPluginHost`, `INamedToolSourceProvider`). Python plugins contribute **tools** rather than a full agent loop, using the MCP stdio protocol. See [polyglot-plugins.md](polyglot-plugins.md).

## What ships

One new library package — `Vais.Agents.Runtime.Plugins` — plus additions to existing packages:

| Package | Addition |
|---|---|
| `Vais.Agents.Runtime.Plugins` *(new)* | `AssemblyPluginLoader`, `IPluginHandlerRegistry`, `PluginAssemblyLoadContext`, `VaisRuntimeAbi`, `AddAgentPlugins(...)` DI extension, 5 loader URNs. |
| `Vais.Agents.Abstractions` | `IAgentHandlerFactory` (plugin-authoring contract), `VaisPluginAttribute` (assembly-level declaration). |
| `Vais.Agents.Control.Abstractions` | `IManifestApplyDiagnosticsSink` (apply-time WARN sink). |
| `Vais.Agents.Core` | `StatefulAgentOptions.Agent` slot — plugin-supplied `IAiAgent` wins over declarative slots. |
| `Vais.Agents.Runtime.Instantiation` | Translator plugin-branch + 2 new URNs (`plugin-factory-throw`, `handler-and-declarative-fields-both-set`). |
| `Vais.Agents.Hosting.Orleans` | `AiAgentGrain.OnActivateAsync` prefers `supplied.Agent` over constructing `StatefulAiAgent`. |

32 packages total (including the five `Vais.Agents.Gateways.*` plugin packages added in the LLM Gateway pillar).

## The pipeline

Nine steps from a plugin DLL dropped on disk to a live agent answering a prompt:

| # | Step | Where |
|---|---|---|
| 1 | `AddAgentPlugins(pluginsDirectory)` registered during composition | `Runtime.Host` |
| 2 | On first `IPluginHandlerRegistry` resolve, `AssemblyPluginLoader.Load` scans subfolders | `Runtime.Plugins` |
| 3 | For each plugin: primary DLL → `PluginAssemblyLoadContext` → `Assembly.LoadFromAssemblyPath` | `Runtime.Plugins` |
| 4 | `[VaisPlugin]` attribute (or convention scan) → `IAgentHandlerFactory` instances registered by `HandlerTypeName` | `Runtime.Plugins` |
| 5 | `vais apply` persists a manifest with `handler.typeName` matching a registered factory | `Control.Http.Server` + `Hosting.Orleans` |
| 6 | `vais invoke` → grain activation → `AgentManifestTranslator.TranslateAsync` | `Runtime.Instantiation` |
| 7 | Translator queries `IPluginHandlerRegistry.TryGet(Handler.TypeName)` before the `Model`-presence check | `Runtime.Instantiation` |
| 8 | Factory `CreateAsync(manifest, sp, ct)` → `IAiAgent` instance → `StatefulAgentOptions.Agent` | `Runtime.Instantiation` |
| 9 | `AiAgentGrain.OnActivateAsync` uses `options.Agent` verbatim; persisted `SystemPrompt` re-applied | `Hosting.Orleans` |

The translator's decision is:

```
  Handler.TypeName matches a loaded plugin?
      │
      ├─ YES ─→ plugin path:
      │         • If Model also set, record apply-time WARN
      │         • factory.CreateAsync → options.Agent
      │         • skip declarative translation entirely
      │
      └─ NO ──→ declarative path:
                • Model set? → v0.17 Pillar B path
                • Model null? → 501 handler-not-loaded
```

Plugin wins when both are set — declarative `Model` / `SystemPromptSpec` / `Tools` / `GuardrailsSpec` fields are ignored. The `IManifestApplyDiagnosticsSink` records a `handler-and-declarative-fields-both-set` warning so the apply response surfaces the silent-ignore to the operator.

## Plugin authoring

Two shapes — pick one:

### Shape A — just implement `IAiAgent`

Simplest case. The loader's convention path auto-wraps your type via `ActivatorUtilities.CreateInstance` so constructor-injected services resolve from the host DI container:

```csharp
// MyApp.WeatherAgent/WeatherAgent.cs
public sealed class WeatherAgent : IAiAgent
{
    private readonly IHttpClientFactory _http;
    private readonly InMemoryAgentSession _session = new("weather", "default");

    public WeatherAgent(IHttpClientFactory http) => _http = http;

    public string? SystemPrompt { get; set; }
    public IAgentSession Session => _session;
    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken ct = default)
    {
        // Your logic — call out, compute, consult a local model, whatever.
        return "Sunny!";
    }

    public void Reset() => _session.ResetAsync().AsTask().Wait();
}
```

Declare the assembly as a plugin:

```csharp
// AssemblyInfo.cs
[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent")]
```

The `targetApiVersion` is matched on major+minor during the 0.x line (`0.18` plugin loads on `0.18.x` runtime); the second argument lists the `AgentManifest.Handler.TypeName` values this plugin advertises.

### Shape B — implement `IAgentHandlerFactory`

When you need per-manifest configuration (inspect `Annotations`, `Labels`, the applied `SystemPrompt`, etc.) or own your factory-time lifecycle:

```csharp
public sealed class WeatherAgentFactory : IAgentHandlerFactory
{
    public string HandlerTypeName => "MyApp.WeatherAgent";

    public async ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        var http = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var region = manifest.Annotations?.GetValueOrDefault("weather.region") ?? "global";
        return new WeatherAgent(http, region);
    }
}
```

The factory path wins over the `IAiAgent` auto-wrap — if your plugin exports both, the factory is used. `CreateAsync` is called **per grain activation** (cold start, idle eviction, post-`UpdateAsync`); memoise expensive state in static / instance fields with thread-safe lazy init, or inject a DI-scoped singleton.

## Isolation model

Each plugin loads into its own non-collectible `PluginAssemblyLoadContext`:

- **Shared types cross the boundary.** Every DI-boundary type (`IAiAgent`, `IServiceProvider`, `IAgentSession`, `ICompletionProvider`, `RunBudget`, `ChatTurn`, MEAI `IChatClient`, `Polly.ResiliencePipeline`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, …) resolves in the runtime's **default** load context so the plugin and the runtime see identical type identities.
- **Plugin-private deps stay per-plugin.** `AssemblyDependencyResolver` picks transitive dependencies (Newtonsoft.Json, RestSharp, custom SDKs) out of the plugin's own folder — two plugins can ship different versions of the same non-shared library without conflict.
- **Non-collectible by design.** Hot-reload is an explicit non-goal for v0.18; plugins load once at silo startup and remain for the process lifetime. Operator updates deploy a new runtime image and cycle the pod.

The shared-types carve-out lives in `PluginAssemblyLoadContext.SharedAssemblies`. Expanding it is a findings-doc change — the carve-out IS the ABI.

## ABI versioning

`VaisRuntimeAbi.CurrentVersion = "0.18"`. Plugin-to-runtime matching:

| Runtime | Plugin `targetApiVersion` | Loaded? |
|---|---|---|
| `0.18` | `0.18` | Yes |
| `0.18.3` | `0.18` | Yes (major.minor match during 0.x) |
| `0.18` | `0.19` | No — `urn:vais-agents:plugin-abi-mismatch`, WARN log, skipped |
| `0.18` | (attribute missing) | If `PluginLoaderOptions.AllowConventionDiscovery = true` (default), fall through to convention scan. Otherwise skipped. |

After v1.0 the match policy tightens to semver-major. The comparison logic lives in `AssemblyPluginLoader.AbiMatches` + `VaisRuntimeAbi.CurrentVersion` — coordinated bumps when shipping breaking Abstractions changes.

## Configuration

One env var:

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_PLUGINS_DIRECTORY` | `/var/lib/vais/plugins` | Absolute path | Unset = default. Explicit empty string = loader disabled (no registry wired; translator skips the plugin branch). Missing / empty / unreadable directory = loader runs as no-op with startup log. |

See [runtime-configuration reference §Plugin loader](../reference/runtime-configuration.md#plugin-loader).

## URN catalogue

Six new URNs ship with v0.18 Pillar C.

### Loader-side (`Runtime.Plugins`)

| URN | Meaning | Typical response |
|---|---|---|
| `urn:vais-agents:plugin-load-failed` | A plugin folder's primary DLL failed `Assembly.LoadFromAssemblyPath` (not a valid PE file, missing native dep, etc.). Logged as WARN; loader continues with the next plugin. | Inspect logs; republish the plugin with `dotnet publish` + its transitive deps. |
| `urn:vais-agents:plugin-abi-mismatch` | Plugin's `targetApiVersion` doesn't match the runtime's `VaisRuntimeAbi.CurrentVersion` under major.minor comparison. WARN log; plugin skipped. | Rebuild the plugin against the runtime's Abstractions version. |
| `urn:vais-agents:plugin-handler-collision` | Two plugins declare the same `HandlerTypeName`. Throws on `PluginLoaderOptions.FailOnHandlerCollision=true` (default); otherwise WARN log + first-registered wins. | Rename one plugin's handler, or pick a dedicated namespace. |
| `urn:vais-agents:plugin-handler-not-found` | A `[VaisPlugin(..., "Foo")]` declared `"Foo"` but the loaded assembly has no matching type. WARN log; handler skipped. | Fix the attribute or the type name mismatch. |

### Translator-side (`Runtime.Instantiation`)

| URN | Meaning | Typical response |
|---|---|---|
| `urn:vais-agents:plugin-factory-throw` | `IAgentHandlerFactory.CreateAsync` threw during grain activation. Wraps the inner exception; status surfaces on the first invoke. `OperationCanceledException` propagates unwrapped. Factory throws do NOT cache a partial result — a retry re-invokes the factory. | Inspect `detail` + `ex.InnerException`; fix the factory logic. |
| `urn:vais-agents:handler-and-declarative-fields-both-set` | **Apply-time WARN, not error.** Manifest declares both a loaded-plugin `TypeName` and declarative `Model` fields. Plugin wins; declarative fields are ignored. Surfaces on the apply response via `IManifestApplyDiagnosticsSink`. | Remove `Model` / `SystemPromptSpec` / `Tools` / `GuardrailsSpec` from the manifest, or retype `handler.typeName: declarative` if the declarative path was intended. |

See [problem-details-urns reference](../reference/problem-details-urns.md) for the complete URN catalogue.

## Security posture

**Plugins run inside the runtime container with the host's full `IServiceProvider`.** A plugin can resolve any registered service — `ISecretResolver`, `IHttpClientFactory`, `IAgentRegistry`, `ILoggerFactory<T>`, database connection factories — and, through that, read secrets, issue outbound traffic, and mutate registry state. Do not load plugins from untrusted sources.

Operational guidance:

- Package plugins into your own runtime image via the overlay pattern (see [package-an-agent-as-a-plugin guide](../guides/package-an-agent-as-a-plugin.md)), not at pod startup from an untrusted volume.
- Pin plugin OCI image digests in production — new images ship as a rolling update, not a mutation of the plugins PVC.
- Audit plugin dependency closures — a compromised transitive pulls in the same DI access as the plugin itself.
- For multi-tenant isolation, run one runtime per trust boundary and use the HTTP control plane's JWT layer for auth. Plugins are a **trusted-code** extension mechanism; they are not a sandbox.

## Hot reload (v0.22)

Set `VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap`. The runtime starts a background `FileSystemWatcher` on the plugins directory. When a new DLL is copied in, `DefaultPluginReloader`:

1. Loads the new DLL into a fresh **collectible** `AssemblyLoadContext`.
2. Atomically swaps the handler registry entry.
3. Calls all registered `IPluginReloadHook` implementations — including `TranslatorInvalidationHook`, which clears the manifest-translator cache for every agent whose `handler.typeName` belongs to the swapped plugin.
4. Calls `Unload()` on the old context so it can be GC-collected.

No pod restart, no manifest re-apply. The policy defaults to `Disabled` (v0.18-compatible).

See the [PluginAgentWeather sample](../../samples/PluginAgentWeather/README.md#hot-reload-without-restarting-the-host-v022) for a worked example.

## Not in scope for v0.18 / still out of scope

- **Non-.NET plugins.** WASM / gRPC / stdio plugin servers are not part of this pillar — the ABI is .NET-only.
- **Sandboxing.** No assembly-level permission capture or service-access blocklists. Trust boundary = runtime pod.
- **Plugin discovery via `vais list-plugins`.** The HTTP surface does not enumerate loaded plugins in v0.18; startup logs record the load manifest.

## Related

- [package-an-agent-as-a-plugin guide](../guides/package-an-agent-as-a-plugin.md) — step-by-step walkthrough from `dotnet new classlib` to `vais invoke`.
- [declarative-agents concept](declarative-agents.md) — the v0.17 path plugins complement; see §"`handler.typeName` coexistence with Pillar C" for the wins-wins-wins decision table.
- [architecture concept §Plugin tier (v0.18 Pillar C)](architecture.md#plugin-tier-v018-pillar-c) — where the loader sits relative to the library stack.
- [runtime-configuration reference §Plugin loader](../reference/runtime-configuration.md#plugin-loader) — env var + appsettings key.
- [problem-details-urns reference](../reference/problem-details-urns.md) — full URN catalogue.
