# v0.18 Plugin model — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.18-plugin-model-spike.md`](./actor-agents-oss-v0.18-plugin-model-spike.md). Answers Q1–Q10 + the flagged open items. Landing verdict at the bottom.

Created 2026-04-21. **Status**: complete. User confirmed the spike's leans on 2026-04-21 ("Proceed to findings and plan"). This doc locks each answer with evidence; the pillar plan then turns decisions into PRs.

---

## Q1 — Where does the plugin-loading code live?

### Evidence

- Survey confirms zero `AssemblyLoadContext` / `Assembly.Load` usage anywhere. No existing plugin scaffolding in `Core` or `Runtime.Instantiation`.
- `Vais.Agents.Core` stays stack-neutral (no `System.Runtime.Loader`, no reflection-driven loading). Putting plugin code in Core would violate the no-deep-deps discipline.
- `Vais.Agents.Runtime.Instantiation` (v0.17) focuses on manifest→options translation. Plugin loading is class-loading + reflection — structurally different; mixing them obscures two concerns.
- Custom hosts (non-`Runtime.Host` consumers) need a reusable plugin loader; putting it inside `Runtime.Host` locks it to that binary.
- Pattern-matches v0.17's decision (Q1 there): "the instantiator needs to be library-consumable."

### Decision (Q1): **New library package `Vais.Agents.Runtime.Plugins`**

`src/Vais.Agents.Runtime.Plugins/` — `Microsoft.NET.Sdk`, `IsPackable=true`, PublicAPI analyzer on, `Directory.Build.props`-inherited warnings + docs. References `Abstractions` + `Core` + `System.Runtime.Loader` (ambient in net9.0). No Orleans dependency; loader is host-agnostic.

---

## Q2 — What does a plugin author implement?

### Evidence

- Partners arriving from ASP.NET Core / Autofac / MEF expect the factory pattern for reliable access to DI, manifest, and ambient config at construction time.
- `IAiAgent` direct implementations (as first-class plugin citizens) would force the loader to ship a second parallel path (`IAgentHandlerFactory` + `IAiAgentClass`), doubling test surface.
- `ActivatorUtilities.CreateInstance<T>` handles the "I just want a trivial type-name → instance" case when wrapped in a stock factory implementation. One contract, both use cases.
- `StatefulAiAgent` has a public ctor (`StatefulAiAgent(ICompletionProvider provider, StatefulAgentOptions? options, ILogger<StatefulAiAgent>? logger)`) — plugin authors who want the standard loop with custom tools return a pre-built `StatefulAiAgent` from their factory.

### Decision (Q2): **`IAgentHandlerFactory` is the primary contract; a trivial auto-wrap supports `IAiAgent`-direct impls**

```csharp
// In Vais.Agents.Abstractions
public interface IAgentHandlerFactory
{
    /// <summary>
    /// Fully-qualified type name this factory produces. Matched (case-insensitive,
    /// ordinal) against <see cref="AgentHandlerRef.TypeName"/> at activation.
    /// </summary>
    string HandlerTypeName { get; }

    /// <summary>
    /// Construct the agent. Called per grain activation (on cold start + after
    /// eviction + after UpdateAsync). Factories that hold expensive state
    /// (SDK clients, loaded models) should cache in their own static fields or
    /// inject a DI-scoped singleton; per-activation cost is per-factory-author.
    /// </summary>
    ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}
```

Auto-wrap path (implemented by the loader, not partner code): if a plugin assembly exports a type `T : IAiAgent` with no matching `IAgentHandlerFactory`, the loader synthesises a default factory:

```csharp
internal sealed class DefaultHandlerFactory<T>(string typeName) : IAgentHandlerFactory where T : IAiAgent
{
    public string HandlerTypeName => typeName;
    public ValueTask<IAiAgent> CreateAsync(AgentManifest manifest, IServiceProvider sp, CancellationToken ct) =>
        new(ActivatorUtilities.CreateInstance<T>(sp));
}
```

Matches the partner mental model: "I write `MyApp.WeatherAgent : IAiAgent`, the runtime figures out the rest."

---

## Q3 — Plugin descriptor format

### Evidence

- Out-of-band JSON sidecars drift. Partners forget to update `plugin.json` when they rename a type; silent breakage follows.
- Assembly-level attributes are strongly typed + colocated with the code that defines the handlers. Refactoring tools (IDE rename + Roslyn fix-ups) keep them accurate.
- Convention-based discovery (scan for `IAgentHandlerFactory` implementations) works for partners testing locally before they add the attribute.
- Three alternatives evaluated; attribute-first matches the `[Serializable]` / `[GenerateSerializer]` pattern partners already see on Orleans grains.

### Decision (Q3): **`[assembly: VaisPlugin(...)]` attribute + convention fallback**

```csharp
// In Vais.Agents.Abstractions
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class VaisPluginAttribute(string targetApiVersion, params string[] handlers) : Attribute
{
    /// <summary><c>Vais.Agents.Abstractions</c> major version the plugin targets (e.g. <c>"0.18"</c>).</summary>
    public string TargetApiVersion { get; } = targetApiVersion;

    /// <summary>AgentHandlerRef.TypeName values this plugin advertises.</summary>
    public IReadOnlyList<string> Handlers { get; } = handlers;
}
```

Partner usage:

```csharp
[assembly: VaisPlugin(
    targetApiVersion: "0.18",
    "MyApp.WeatherAgent", "MyApp.TicketingAgent")]
```

Loader behaviour:
1. Scan each plugin DLL for the assembly attribute. **With attribute** → ABI check + factory discovery.
2. **Without attribute** → convention scan — look for any public `IAgentHandlerFactory` implementation. Synthesise a default descriptor with `TargetApiVersion = <runtime current>` + handlers = factory names. Log INFO "plugin X loaded via convention (no VaisPlugin attribute)."

The attribute is recommended (produces versioned, forward-compatible descriptors). Convention is a partner-friendly fallback for "drop a DLL + see it work."

---

## Q4 — AssemblyLoadContext isolation model

### Evidence

- Survey: no existing `AssemblyLoadContext` usage. Greenfield.
- Per-plugin `AssemblyLoadContext` is the standard .NET pattern (ASP.NET Core uses it for dynamic plugin-loading; Autofac-style MEF hosts use it for assembly isolation).
- Default context — plugin loaded alongside runtime types — breaks as soon as a plugin ships a different Newtonsoft.Json / MEAI / OpenTelemetry than the runtime.
- Shared-types carve-out is the tricky bit: `Vais.Agents.Abstractions.IAiAgent` MUST resolve to the runtime's assembly, not a copy the plugin ships; otherwise `(IAiAgent)factory.Create(...)` throws `InvalidCastException`.
- Non-collectible is simpler + faster. Pillar 3 master plan's non-goal list excludes hot-reload; collectibility is not needed in v0.18.

### Decision (Q4): **Per-plugin `PluginAssemblyLoadContext` (non-collectible) with explicit shared-types carve-out**

```csharp
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.Ordinal)
    {
        // Vais.Agents ABI surface — types crossing the plugin boundary.
        "Vais.Agents.Abstractions",
        "Vais.Agents.Core",
        "Vais.Agents.Control.Abstractions",

        // DI + hosting + logging abstractions — plugins receive these from the runtime.
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Configuration.Abstractions",

        // MEAI — IChatClient types flow between plugin + runtime-provided completion providers.
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.AI.Abstractions",

        // Polly — StatefulAgentOptions.ResiliencePipeline is a cross-boundary type.
        "Polly.Core",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginRootAssembly)
        : base(name: Path.GetFileNameWithoutExtension(pluginRootAssembly), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginRootAssembly);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
        {
            // Defer to Default context — runtime's version wins.
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
```

**Shared-types list** frozen in v0.18; additions require a findings-doc amendment. Plugins that need a shared type added file an issue; partners don't hack the loader.

**Collectibility**: `isCollectible: false`. Revisit if Phase 4 adds hot-reload.

---

## Q5 — Plugin directory layout

### Evidence

- `dotnet publish` emits all transitive dependency DLLs alongside the primary assembly into a single output directory. Flat layout means one plugin's deps collide with another's (e.g., two plugins with different Newtonsoft versions → loader sees only whichever DLL loaded first).
- Per-plugin subfolder keeps transitive deps contained + matches what `dotnet publish -o my-plugin/` produces.
- `AssemblyDependencyResolver(pluginRootAssembly)` reads `MyPlugin.deps.json` next to the primary DLL — published output carries this naturally.
- Zip / nupkg archives add unzipping complexity + temp-dir hygiene. Partners package via `docker cp` / Helm ConfigMap mount / PVC-seeding init container; no archive handling at runtime.

### Decision (Q5): **Per-plugin subfolder layout**

Directory structure:
```
/var/lib/vais/plugins/
├── weather-agent/
│   ├── MyApp.WeatherAgent.dll        (+ primary assembly)
│   ├── MyApp.WeatherAgent.deps.json  (transitive-dep map)
│   ├── Newtonsoft.Json.dll           (plugin's own transitive)
│   └── ...
└── ticketing-agent/
    ├── MyApp.TicketingAgent.dll
    ├── MyApp.TicketingAgent.deps.json
    └── ...
```

Loader behaviour:
1. Enumerate `/var/lib/vais/plugins/*/` — one subfolder per plugin.
2. For each subfolder: pick the non-`deps.json`, non-system `.dll` (glob `*.dll` minus common framework / abstractions files) as the primary assembly, using assembly-name ⇔ folder-name match as a tiebreaker. **Lean**: if multiple DLLs exist, require a `<folder-name>.dll` naming convention. Folder `weather-agent/` → primary `weather-agent.dll`. Fallback: check `[assembly: VaisPlugin(...)]` attribute across candidates.
3. Construct a fresh `PluginAssemblyLoadContext` per subfolder; load the primary assembly; recursively resolve transitive deps via `AssemblyDependencyResolver`.
4. Parse `[assembly: VaisPlugin]` attribute (or fall through to convention discovery per Q3).

---

## Q6 — How does the plugin loader plug into grain activation?

### Evidence

- Pillar B added `StatefulAgentOptions.CompletionProvider` so the translator could stash per-agent providers for the grain to pick up. Same shape applies here.
- Alternative "dedicated `Func<string, IAiAgent?>`" pattern (C in the spike) adds a second registration point parallel to the existing options factory — two orthogonal slots; grain has to check both.
- Direct registry-query (B) splits the "what options does this agent need" concern across two layers (translator + grain), making failure paths harder to trace.
- Translator-produced options + slot on options matches Pillar B's mental model: "translator translates, grain wraps."

### Decision (Q6): **New `StatefulAgentOptions.Agent` init-only slot — translator stashes, grain prefers**

```csharp
public sealed class StatefulAgentOptions
{
    // ...existing fields (AgentName, CompletionProvider, SystemPrompt, ToolRegistry, …)

    /// <summary>
    /// Pre-constructed agent supplied by the manifest instantiation pipeline
    /// (v0.18 plugin model). When set, host-side grain activation uses this
    /// instance verbatim instead of constructing <c>StatefulAiAgent</c> from
    /// the declarative slots. Null ⇒ fall through to the v0.17 declarative
    /// path (CompletionProvider + SystemPrompt + ToolRegistry + ...).
    /// </summary>
    public IAiAgent? Agent { get; init; }
}
```

Grain activation (v0.18):
```csharp
public override Task OnActivateAsync(CancellationToken cancellationToken)
{
    var id = this.GetPrimaryKeyString();
    var supplied = _optionsFactory(id);

    if (supplied.Agent is not null)
    {
        // Plugin-produced agent — take it verbatim. Pillar C.
        _agent = supplied.Agent;
        ApplyPersistedState(supplied.Agent, _state.State);
        return base.OnActivateAsync(cancellationToken);
    }

    // Pillar B / v0.16 declarative path unchanged.
    var provider = supplied.CompletionProvider ?? _defaultProvider
        ?? throw new InvalidOperationException(...);
    var seeded = /* ... */;
    _agent = new StatefulAiAgent(provider, seeded, _loggerFactory.CreateLogger<StatefulAiAgent>());
    return base.OnActivateAsync(cancellationToken);
}
```

`ApplyPersistedState` handles the SystemPrompt + History hydration for plugin-produced agents consistently — plugins that don't care about state just ignore the state the grain hands them. Plugins that want custom state use their factory's constructor injection (the grain state persists the standard `AiAgentGrainState` shape).

Pillar B test `Composition_Translator_Registered_For_ConfigureAgentGrains` extends to cover the plugin path: "options.Agent is set when manifest references a loaded plugin" + "grain uses options.Agent verbatim."

---

## Q7 — AgentHandlerRef.TypeName coexistence with Model

### Evidence

- Pillar B findings Q10 committed to "typeName wins when both set" — deferred the enforcement to Pillar C since typeName had no implementation.
- Pillar C has the implementation now; decision needs to hold.
- Fail-on-both-set would force partners migrating from pure-declarative to mixed-plugin+declarative (e.g., adding a custom tool loop but keeping manifest-driven prompts) to choose one path explicitly. User-hostile.
- WARN at apply time surfaces the conflict without blocking.

### Decision (Q7): **Plugin wins when both `Model` + matching `TypeName` present; WARN at apply**

| `Model` | `handler.TypeName` → loaded plugin? | v0.18 behaviour |
|---|---|---|
| null | yes | Plugin path. Declarative fields (guardrails, budget) applied where plugin-returned agent is a `StatefulAiAgent`-derived / wrapping type; ignored otherwise (plugin owns its loop). |
| null | no | `501 urn:vais-agents:handler-not-loaded` at invoke (apply still accepts — partner may load the plugin later via pod restart). |
| null | sentinel `"declarative"` | `400 urn:vais-agents:manifest-invalid` at apply — same as v0.17 (neither path resolvable). |
| set | yes | Plugin wins. Apply-time WARN `handler-and-declarative-fields-both-set` on the response. CLI prints `warn:` to stderr + exits 0. |
| set | no / sentinel | Declarative path (Pillar B). |
| set | yes + apply-time validator | If strict-validation mode enabled (future; not v0.18 default), apply fails with `400 urn:vais-agents:manifest-ambiguous`. |

### Decision subcomment — apply-time plugin resolution

Apply-time validation runs `handlerRegistry.TryGet(manifest.Handler.TypeName, out _)` — a fast registry-hashmap lookup — and emits the WARN if both-set. No factory invocation at apply; that happens at first activation.

If the plugin later loads after the manifest was already applied, subsequent activations pick it up. This matches partner expectations: "deploy new plugin DLL via image update / PVC refresh → next invoke routes through it."

---

## Q8 — Partner plugin-discovery flow

### Evidence

- HTTP endpoint `GET /v1/plugins` requires Pillar B's HTTP-surface wiring, manifest-validator updates, CLI plumbing, tests. Doable but adds surface.
- Runtime startup already emits structured INFO logs for every pillar feature ("Vais.Agents runtime starting — mode=… clustering=… opa=…"). Adding `plugins=N loaded` + per-plugin INFO lines fits the same pattern.
- Partners running in Docker / K8s already have `docker logs` / `kubectl logs` as their debugging tool.
- `/v1/plugins` adds runtime-introspection value but can ship as v0.18.x polish without blocking v0.18's invoke-returns-real-answer acceptance.

### Decision (Q8): **Startup-log introspection only in v0.18; `/v1/plugins` endpoint is v0.18.x polish**

Startup log additions in `Runtime.Host`:
```
info: Vais.Agents[0]  Vais.Agents runtime starting — mode=localhost clustering=n/a opa=disabled (AllowAll) otel=disabled langfuse=disabled plugins=2
info: Vais.Agents.Runtime.Plugins[0]  Loaded plugin 'weather-agent' (targetApiVersion=0.18, handlers=[MyApp.WeatherAgent])
info: Vais.Agents.Runtime.Plugins[0]  Loaded plugin 'ticketing-agent' (targetApiVersion=0.18, handlers=[MyApp.TicketingAgent])
```

Failure-to-load logs at WARN with the URN + reason:
```
warn: Vais.Agents.Runtime.Plugins[0]  Plugin 'experimental/' failed: urn:vais-agents:plugin-abi-mismatch — attribute targets 0.17, runtime is 0.18
```

No `/v1/plugins` endpoint in v0.18; add in v0.18.1 alongside a CLI `vais plugins list` command once partners ask.

---

## Q9 — Failure modes and URNs

### Evidence

Each failure path has a clear partner-diagnosable URN. Mapped to HTTP Problem Details via the existing `VaisProblemDetailsOperationTransformer`.

### Decision (Q9): **Six new URNs landing in `ManifestInstantiationUrns`**

| URN | When | HTTP status | Surface |
|---|---|---|---|
| `urn:vais-agents:plugin-load-failed` | Plugin DLL exists but CLR refused to load (missing dep, bad IL, incompatible runtime). | 500 at invoke (if referenced by manifest); startup log WARN otherwise. | Runtime log + invoke response |
| `urn:vais-agents:plugin-abi-mismatch` | `[assembly: VaisPlugin(targetApiVersion = "0.X")]` doesn't match the runtime's current ABI major. | Plugin not loaded; startup log WARN. Manifests referencing its handlers hit `handler-not-loaded` at invoke. | Runtime log |
| `urn:vais-agents:plugin-handler-collision` | Two loaded plugins export the same `AgentHandlerRef.TypeName`. | Fail-fast startup exception — partner must rename or remove one. | Runtime crash + structured log |
| `urn:vais-agents:plugin-handler-not-found` | Manifest references a `TypeName` no loaded plugin owns (and no `Model` set). | 400 at apply (strict mode, future) OR 501 at invoke (v0.18 default) via `handler-not-loaded` cascade. | Apply response or invoke response |
| `urn:vais-agents:plugin-factory-throw` | `IAgentHandlerFactory.CreateAsync` threw during activation. | 500 at invoke. Inner exception type in Problem Details extensions. | Invoke response + runtime log |
| `urn:vais-agents:handler-and-declarative-fields-both-set` | Apply-time WARN. Plugin takes precedence. | 200 at apply with `warnings: [...]` in response. | Apply response |

The existing `handler-not-loaded` URN (from v0.17) stays — now reached by the "plugin for this TypeName never loaded / was unloaded / ABI mismatched" path.

---

## Q10 — Secret + DI access from plugin code

### Evidence

- Partners arriving from ASP.NET Core background expect middleware-style DI access (`IServiceProvider` for anything goes).
- Restricted `IPluginContext` requires the loader to maintain a whitelist of services; every partner who needs a new service files an issue / ships a PR — drag.
- No security boundary inside the process — plugins can read environment variables + make outbound HTTP directly regardless of DI restrictions. Restricting DI is security-theatre.
- Documented "contract surface" — services we guarantee are registered — gives partners a soft contract without invention.

### Decision (Q10): **Full `IServiceProvider` + documented contract surface**

Contract surface (services guaranteed registered when the runtime host composes DI):
- `ISecretResolver` — env + file + composite (+ K8s via `file://` against projected-token volumes).
- `IHttpClientFactory` — standard ASP.NET Core typed-client factory.
- `ILogger<T>` — for any T.
- `IAgentRegistry` — read-only access to all registered manifests.
- `IAgentContextAccessor` — ambient context for the current invocation.
- `TimeProvider.System` — injectable clock, matches Microsoft convention.
- Every `Vais.Agents.*` service registered by `CompositionRoot.ConfigureServices` (documented in [runtime-configuration reference](../oss/agentic/docs/reference/runtime-configuration.md)).

Plugin factories resolve whatever they need:
```csharp
public sealed class WeatherAgentFactory : IAgentHandlerFactory
{
    public string HandlerTypeName => "MyApp.WeatherAgent";

    public async ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var secrets = sp.GetRequiredService<ISecretResolver>();
        var logger = sp.GetRequiredService<ILogger<WeatherAgent>>();

        var apiKey = await secrets.ResolveAsync("secret://env/WEATHER_API_KEY", ct);
        return new WeatherAgent(httpFactory.CreateClient(), apiKey, logger);
    }
}
```

Plugins that want typed options register them in their factory's DI extension (runtime picks it up because the plugin factory is DI-resolved via its assembly-loaded type).

---

## Open items resolved

### ABI version parsing

Committed: **major-version-only match**. Plugin attribute `TargetApiVersion = "0.18"` loads on any 0.18.x runtime; fails to load on 0.19.x. The loader parses the version as `Version.Parse` + compares `.Major` + `.Minor` (since we're still in pre-1.0, `0.X` is the major-equivalent).

Startup log spells the comparison: `"Plugin 'weather-agent' targets ABI 0.18; runtime ABI 0.18 — compatible"`.

### Apply-time fail-fast vs. lazy-404

Committed: **fail-apply on unknown TypeName (strict default, v0.18.x polish)**.

For v0.18, apply accepts manifests whose `TypeName` points at plugins that haven't loaded yet — this matches the partner-friendly deploy-plugin-later flow. Invoke hits `handler-not-loaded` (existing v0.17 URN) until the plugin is in place.

Strict validation (`urn:vais-agents:plugin-handler-not-found` at apply) is a polish item — ship as `--strict` flag on `vais apply` or via a runtime config toggle in v0.18.x.

### Startup scan timing

Committed: **synchronous on silo boot**. The plugin scan + load completes before `Runtime.Host` marks `IsReady`; readiness probe still gates on `Orleans.SiloStatus == Active`. If scan exceeds 5 seconds per plugin, startup log emits a WARN + continues; failing plugins add to a `FailedPlugins` counter.

Async/parallel scan is a polish item.

### Factory creation on grain activation

Committed: **factory invoked per activation**, cache is the plugin author's responsibility. Grain deactivation on idle (default Orleans behaviour) triggers factory re-invoke on next use. Plugin factories that hold expensive state should use DI-scoped singletons + inject them, not cache in per-factory statics.

Documented in `docs/concepts/runtime-plugins.md` under "Lifecycle + performance."

### Non-.NET plugins

Deferred. Partners with Python / Node agents use:
- **A2A cross-runtime refs** (Pillar E / v0.20) — their agent runs in its own runtime; our runtime references it via A2A.
- **HTTP invoke from another process** — external caller hits `/v1/agents/{id}/invoke` directly.

Sidecar containers (non-.NET plugins running alongside the runtime pod) remain a Phase-4 open question.

---

## Landing verdict

All ten blocking questions and four open items resolved with evidence. No new open questions surfaced during findings.

**Scope for v0.18.0-preview is now frozen:**

1. New `Vais.Agents.Runtime.Plugins` library package.
2. `IAgentHandlerFactory` (primary contract) + `VaisPluginAttribute` (ABI + handler declaration) + auto-wrap for `IAiAgent`-direct impls.
3. Per-plugin non-collectible `PluginAssemblyLoadContext` with shared-types carve-out (Vais.Agents ABI + DI / hosting / logging / options / MEAI / Polly).
4. Per-plugin subfolder layout under `/var/lib/vais/plugins/`.
5. `StatefulAgentOptions.Agent` slot — translator stashes plugin-produced agent, grain prefers it.
6. Plugin wins when both `Model` + `TypeName` set; apply-time WARN.
7. Startup-log introspection (six new URNs in `ManifestInstantiationUrns`).
8. Full `IServiceProvider` access for factories + documented contract-surface.

**Out of scope explicitly:**

- Sidecar-container plugins (Phase 4).
- OCI-layer plugins (covered as build-time convention via sample Dockerfile).
- Hot reload (master-plan non-goal).
- Cross-ABI compat (plugins declare target-major + loader enforces).
- `/v1/plugins` endpoint + CLI `vais plugins list` (v0.18.x polish).
- Strict apply-time validation of TypeName presence (v0.18.x polish).
- Plugin signing / SBOM / supply-chain verification (Pillar F).

**Ready for pillar plan.** Spike → findings cycle complete; next doc is `plans/actor-agents-oss-v0.18-plugin-model-pillar.md` — locks the 4-PR sequence with per-PR checklists, acceptance criteria, and timeline.
