# v0.18.0-preview — Plugin model for code-authored agents (Pillar C) — spike

Open-questions research doc for [Phase 3 Pillar C](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-c--plugin-model-for-code-authored-agents-us-2-primary). Answers partner user-story US-2 (create an agent by code + deploy via manifest). Unblocks manifests that set `handler.typeName` to a real .NET type name — today those trip `501 urn:vais-agents:handler-not-loaded`.

Written 2026-04-21. Precedes the findings + pillar plan of the same name.

---

## What v0.17 shipped, what's still 501 for handler-bound manifests

Pillar B (v0.17) ships the declarative path: manifests with `Model != null` flow through `AgentManifestTranslator` to `StatefulAgentOptions` → `StatefulAiAgent`. Manifests **without** `Model` hit this early-out in `AgentManifestTranslator.TranslateAsync` (AgentManifestTranslator.cs:68–76):

```csharp
if (manifest.Model is null)
{
    throw new ManifestInstantiationException(
        ManifestInstantiationUrns.HandlerNotLoaded,
        $"Agent '{agentId}' has no ModelSpec and no declarative fields...");
}
```

The manifest's `AgentHandlerRef(string TypeName, string? AssemblyName = null)` exists as wire-format today — persisted, K8s-CRD-mirrored, idempotency-key-stable — but **nothing reads it**. Zero references across 26 packages. Zero `AssemblyLoadContext` / `Assembly.Load` usage anywhere. Pillar C owns the full design.

The runtime host has already baked three conventions Pillar C consumes:
- `/var/lib/vais/plugins` volume + FHS-compliant chown in the Dockerfile (Pillar A).
- Helm chart `plugins.enabled` + `plugins.persistentVolumeClaimName` + emptyDir fallback (Pillar A).
- `AiAgentGrain.OnActivateAsync` already takes `Func<string, StatefulAgentOptions>` from `ConfigureAgentGrains` (Pillar B).

The pillar plan's thesis — **DLL plugins first, sidecar deferred** — stands after the survey. DLL is the .NET-native answer for partners; sidecar becomes necessary only when non-.NET hosting lands (Pillar E cross-runtime refs can cover much of that via A2A today).

---

## Scope fence — what ships in v0.18 vs. deferred

**v0.18 ships** the pure-.NET DLL-plugin path. Specifically:

- **`Vais.Agents.Runtime.Plugins` package** (new) — `AssemblyPluginLoader`, `PluginDescriptor`, per-plugin `AssemblyLoadContext` isolation.
- **Plugin descriptor**: `plugin.json` sidecar advertising exported `AgentHandlerRef.TypeName` strings + the `Vais.Agents.Abstractions` API version the plugin targets.
- **`IAgentHandlerFactory` contract** (new, in `Vais.Agents.Abstractions`) — plugin authors implement this; receives manifest + `IServiceProvider`; returns `IAiAgent` (custom or `StatefulAiAgent`-derived).
- **Runtime-host wiring**: `appsettings.json` gains a `Plugins.Directory` knob; composition root scans + loads on startup.
- **`AgentManifestTranslator` path**: when `handler.TypeName` matches a loaded plugin, wire the translator to produce a `StatefulAgentOptions` with a pre-supplied `IAiAgent` (new options slot); otherwise fall back to declarative / 501 as today.
- **`AiAgentGrain` extension**: new `Func<string, IAiAgent?>` or options-slot path — prefers plugin-produced agent over constructing `StatefulAiAgent`.
- **Dockerfile overlay convention**: `Dockerfile.overlay` sample showing `COPY /publish/plugins /plugins`.
- **Sample**: `samples/PluginAgentWeather/` — one consumer-authored `IAiAgent` implementation packaged as a plugin.
- **Docs**: `docs/concepts/runtime-plugins.md` + `docs/guides/package-an-agent-as-a-plugin.md`.

**Explicitly deferred** to v0.18.x / Pillar E / Pillar F:

- **Sidecar-container plugins** (non-.NET, hard isolation) — deferred; partners wanting Python / TS agents use the A2A bridge from Pillar E.
- **OCI-layer plugins** — treated as a build-time convention (`COPY --from=plugin-image /out /plugins`), not a runtime-loaded layer. The sample's `Dockerfile.overlay` demonstrates it.
- **Hot reload** — Pillar 3 master-plan non-goal. Plugins load at silo start; filesystem changes require restart.
- **Cross-version plugin compat** — plugins must declare + match the current `Vais.Agents.Abstractions` major version. Load fails if mismatch.
- **Plugin capability negotiation** — plugin either implements `IAiAgent` or it doesn't; no "optional capabilities" advertisement beyond `IStreamingAiAgent` inheritance.
- **Plugin signing / verification** — polish, Pillar F.
- **Kubernetes SA-token scoped plugin dirs** — out-of-scope; plugins access DI as if co-hosted.

---

## Blocking questions (10)

### Q1 — Where does the plugin-loading code live?

**Context.** The survey confirms no existing plugin scaffolding. Three reasonable homes:

- **A. New package `Vais.Agents.Runtime.Plugins`** — library-layer, `IsPackable=true`. Parallel to `Runtime.Instantiation`.
- **B. Inside `Vais.Agents.Runtime.Host`** — non-packable. Keeps plugin machinery private to the runtime binary.
- **C. Extend `Vais.Agents.Runtime.Instantiation`** — translator + plugin loader in one package.

**Lean: A.** Plugin loader needs to be library-consumable because (a) custom hosts (not using `Runtime.Host`) should be able to host plugins too, (b) tests drive it without Orleans, (c) `Runtime.Instantiation` stays focused on manifest→options translation — plugin loading is structurally different (class loading, not config interpretation).

### Q2 — What does a plugin author implement?

**Context.** Two candidate entry points:

- **A. `IAiAgent` directly** — plugin author writes `MyApp.WeatherAgent : IAiAgent`; loader instantiates via reflection, grain wraps verbatim. Simple for partners who want to fully own the agent loop.
- **B. `IAgentHandlerFactory`** — plugin author writes `MyApp.WeatherAgentFactory : IAgentHandlerFactory`; factory.Create(manifest, sp) returns an `IAiAgent`. Author can choose to return `new StatefulAiAgent(...)` with custom options, or fully-custom.
- **C. Both** — plugin's `plugin.json` declares which pattern each exported handler uses.

**Lean: B with an auto-wrap path for A.** `IAgentHandlerFactory` is the primary contract because (a) most partners will want access to DI / manifest / ambient config at construction time, (b) the factory can return `StatefulAiAgent`-backed agents when the partner wants the standard loop but custom tools, (c) a plugin that wants the A-shape just ships a trivial factory that `new`s the type via `ActivatorUtilities.CreateInstance`. One contract, zero extra partner work for the "type-name only" case.

```csharp
// In Vais.Agents.Abstractions
public interface IAgentHandlerFactory
{
    /// <summary>Fully-qualified type name this factory produces. Matches AgentHandlerRef.TypeName.</summary>
    string HandlerTypeName { get; }

    /// <summary>Construct the agent for the given manifest. Called per grain activation.</summary>
    ValueTask<IAiAgent> CreateAsync(AgentManifest manifest, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}
```

### Q3 — Plugin descriptor format

**Context.** The loader needs to know, for each plugin DLL, which `AgentHandlerRef.TypeName` strings it owns and which ABI version it targets.

- **A. `plugin.json` sidecar** — machine-readable, authors manage by hand, no in-assembly dependencies. Matches conventions in Kubernetes / containerd / OPA bundles.
- **B. Assembly-level attribute** — `[assembly: VaisPlugin(ApiVersion = "0.18.0", Handlers = ["MyApp.WeatherAgent"])]`. Strongly typed, no extra file, but requires tooling to emit the attribute.
- **C. Convention-based discovery** — scan the DLL for types implementing `IAgentHandlerFactory`, no descriptor. Simplest but no ABI pinning.

**Lean: B + C fallback.** Assembly attribute for the normal case (source-of-truth lives with the code); convention fallback scans `IAgentHandlerFactory` implementations without the attribute (for partners testing locally). Skip `plugin.json` — it adds out-of-band state that partners forget to update.

```csharp
[assembly: VaisPlugin(
    TargetApiVersion = "0.18",
    Handlers = new[] { "MyApp.WeatherAgent" })]
```

Version mismatch → plugin load fails with `urn:vais-agents:plugin-abi-mismatch`. Partners bump the attribute when they update the `Vais.Agents.Abstractions` reference.

### Q4 — AssemblyLoadContext isolation model

**Context.** Plugins may bring transitive dependencies that conflict with the runtime's own versions (SK, MAF, OpenTelemetry, Newtonsoft.Json, etc.).

- **A. Default `AssemblyLoadContext`** — simplest, plugin loaded in the same context as the runtime. Version conflicts crash at JIT time.
- **B. Collectible per-plugin `AssemblyLoadContext`** — full isolation; each plugin sees its own dependency graph. Shared types (`IAiAgent`, `AgentManifest`) must resolve to the *runtime's* assembly, not duplicates the plugin might ship.
- **C. Collectible, non-collectible, or isolated** — offer partners a knob.

**Lean: B with explicit shared-types policy.** Partner expectation is "my plugin doesn't break because I shipped a newer Newtonsoft than the runtime." The per-plugin context resolves requests for `Vais.Agents.Abstractions` / `Vais.Agents.Core` / `Microsoft.Extensions.*` from the runtime's default context; everything else loads from the plugin folder. This matches how ASP.NET's plugin loaders + Autofac-style MEF hosts work.

Implementation: `PluginAssemblyLoadContext : AssemblyLoadContext` with a `_sharedAssemblies` hash-set covering the ABI surface + MEAI + DI extension abstractions.

Plugins aren't collectible in v0.18 — hot-reload is a non-goal. Non-collectible is simpler + faster.

### Q5 — Plugin directory layout

**Context.** One folder per plugin? Flat DLLs in `/var/lib/vais/plugins`?

- **A. Flat** — all DLLs in one directory. Simple scan; naming collisions risk.
- **B. Per-plugin subfolder** — `/var/lib/vais/plugins/weather-agent/{MyApp.WeatherAgent.dll + transitive deps}`. Matches the way NuGet packages hydrate.
- **C. Per-plugin zip / nupkg** — compressed archive; loader unzips to temp.

**Lean: B.** Per-plugin subfolder keeps transitive dep DLLs contained and matches what `dotnet publish` produces by default. Scan `/var/lib/vais/plugins/*/` for subfolders, inspect each for a primary DLL + optional `plugin.dll` alias. No archive handling (partners handle packaging via `docker cp` / Helm config-map mount / PVC-seeding init-container).

### Q6 — How does the plugin loader plug into grain activation?

**Context.** The survey found `AiAgentGrain.OnActivateAsync` hardcodes `new StatefulAiAgent(provider, seeded, logger)`. Pillar C needs to teach it to use a plugin-produced `IAiAgent` when the manifest's handler matches a loaded plugin.

- **A. New options slot** — add `IAiAgent? Agent { get; init; }` to `StatefulAgentOptions`; translator stashes plugin-produced agent there; grain prefers it. Mirrors Pillar B's `CompletionProvider` slot.
- **B. Direct registry** — runtime maintains `IPluginHandlerRegistry` keyed on `TypeName`; grain queries it alongside options. Separate concern.
- **C. Dedicated Func** — another `Func<string, IAiAgent?>` slot wired into `ConfigureAgentGrains`. Parallel to `Func<string, StatefulAgentOptions>`.

**Lean: A.** Matches Pillar B's shape (grain reads `supplied.Agent ?? supplied.CompletionProvider` path); translator drives the decision; plugin loader populates the registry the translator consults. One mutating point, easy to reason about, one new test.

Grain flow after Pillar C:
```csharp
var supplied = _optionsFactory(id);  // translator
var agent = supplied.Agent                    // plugin-produced (Pillar C)
    ?? BuildStatefulAgent(supplied, provider); // declarative (Pillar B)
```

### Q7 — AgentHandlerRef.TypeName coexistence with Model

**Context.** v0.17 says `Model != null` = declarative path. v0.18 adds plugins. What about manifests with both? With neither?

| `Model` | `handler.TypeName` | v0.18 behaviour |
|---|---|---|
| null | matches a loaded plugin | Plugin path — factory produces the agent. |
| null | does not match any plugin | `501 urn:vais-agents:handler-not-loaded` (same as v0.17). |
| set | matches a plugin | Plugin wins. Apply-time WARN `handler-and-declarative-fields-both-set`. |
| set | no match / `declarative` sentinel | Declarative path (v0.17 behaviour). |

**Lean: plugin wins when both are set; WARN surfaces at apply time.** Matches the Pillar B findings Q10 semantic — code beats config because code is more specific. Document that the `declarative` sentinel string is reserved for "no plugin bind" pending a richer mechanism (post-v0.18 perhaps a schema-validated enum).

### Q8 — Partner plugin-discovery flow

**Context.** Partner drops a plugin DLL into the runtime. How do they verify it loaded?

- **A. Silent load + startup log** — runtime logs each plugin + handler at INFO. Partners grep logs.
- **B. HTTP endpoint `GET /v1/plugins`** — lists loaded plugins + handlers + ABI version. Polished.
- **C. CLI `vais plugins list`** — hits the endpoint above.

**Lean: A for v0.18; B + C as v0.18.x polish.** Get the loader working end-to-end with just startup-log introspection first. The `/v1/plugins` endpoint is an add-on that doesn't block the core feature; partners with containers have `docker logs` / `kubectl logs` as the default tool.

### Q9 — Failure modes and URNs

New URNs for Pillar C — ship in `ManifestInstantiationUrns` + mapped to HTTP Problem Details:

- `urn:vais-agents:plugin-load-failed` — DLL exists but CLR refused to load it (bad assembly, missing dep). Logged + fatal if the plugin is the only handler for a referenced manifest; ignored otherwise.
- `urn:vais-agents:plugin-abi-mismatch` — assembly attribute reports a different ABI version than the runtime's `Vais.Agents.Abstractions` major version. Plugin not loaded.
- `urn:vais-agents:plugin-handler-collision` — two plugins export the same `TypeName`. Fail fast at startup; partner must rename or remove one.
- `urn:vais-agents:plugin-handler-not-found` — manifest references a `TypeName` no loaded plugin owns. Surfaces at apply time (validation) + invoke time (fallback).
- `urn:vais-agents:plugin-factory-throw` — `IAgentHandlerFactory.CreateAsync` threw. Surface to the invoke caller with partial details; log the full stack.

### Q10 — Secret + DI access from plugin code

**Context.** A custom `IAiAgent` may want to read secrets or resolve services from DI. Partners will reach for `ISecretResolver`, `IHttpClientFactory`, etc.

- **A. Full DI access** — factory receives the live `IServiceProvider`; plugin resolves whatever it needs.
- **B. Restricted surface** — factory receives a narrower `IPluginContext` type; plugin sees only pre-approved services.
- **C. Per-plugin child scope** — `IServiceProvider` scoped to the plugin for disposal tracking.

**Lean: A + documented convention.** Plugins get the full `IServiceProvider`. Document the "contract surface" — the services we guarantee are registered (`ISecretResolver`, `IHttpClientFactory`, `ILogger<T>`, `IAgentRegistry`). Partners who want disposability register their own `IDisposable` services via `AddScoped` and resolve them inside the factory; we don't invent a restricted API.

Equivalent to how ASP.NET Core exposes DI inside middleware + filters — simplest + most familiar model.

---

## Proposed PR shape

Four-PR sequence inside `v0.18.0-preview`. Each independently shippable.

### PR 1 — `Vais.Agents.Runtime.Plugins` package + contracts

- [ ] Create `src/Vais.Agents.Runtime.Plugins/` library project.
- [ ] Add `IAgentHandlerFactory` contract to **`Vais.Agents.Abstractions`** (layering: plugin impls live in consumer code + `Abstractions`; loader lives in new `Runtime.Plugins` package).
- [ ] `VaisPluginAttribute` (assembly-level) — `TargetApiVersion: string`, `Handlers: string[]`.
- [ ] `PluginDescriptor` record — what the loader extracts per plugin (assembly path, ABI version, handler → factory-type map).
- [ ] `IPluginHandlerRegistry` contract + `PluginHandlerRegistry` impl — keyed on `TypeName`; factory lookup.
- [ ] `PluginAssemblyLoadContext` — per-plugin `AssemblyLoadContext` with runtime-shared types carve-out.
- [ ] `AssemblyPluginLoader` — scans a directory, loads each `*/.dll`, parses the `VaisPluginAttribute`, registers factories with the handler registry.
- [ ] Plugin-specific exception: `PluginLoadException` with the 5 URNs from Q9.
- [ ] `AddAgentPlugins(IServiceCollection, string directory)` DI extension.
- [ ] `StatefulAgentOptions.Agent` slot — new `IAiAgent?` init-only property (Core, additive; PublicAPI *REMOVED* + new).
- [ ] `AiAgentGrain.OnActivateAsync` — prefer `supplied.Agent` over constructing `StatefulAiAgent` (Hosting.Orleans, additive behavioural change).
- [ ] 15+ unit tests covering: plugin-folder scan, load-success / load-failure / ABI-mismatch / handler-collision, factory lookup, per-plugin isolation (two plugins importing different Newtonsoft versions), `StatefulAgentOptions.Agent` picked over `StatefulAgentOptions.CompletionProvider`.
- [ ] Build clean; 0 warnings.

### PR 2 — Translator integration + URN wiring + manifest validator

- [ ] `AgentManifestTranslator` — new branch: before the current `Model is null` check, query `IPluginHandlerRegistry` for `manifest.Handler.TypeName`. If present, call `factory.CreateAsync(manifest, sp)` and stash result in `options.Agent`.
- [ ] Both-set case: `Model` set + `TypeName` matches a plugin → plugin wins, WARN surfaces via new `IManifestApplyDiagnosticsSink` (lightweight; logs WARN + records on an enumerable).
- [ ] New URNs in `ManifestInstantiationUrns` — the 5 from Q9 + `handler-and-declarative-fields-both-set`.
- [ ] Tests: plugin-match path returns options with `Agent` set; no-plugin-match with `Model` set falls through to declarative; no-plugin-match with no `Model` still 501s; both-set triggers WARN + plugin wins.

### PR 3 — Runtime host wiring + Dockerfile overlay pattern + sample

- [ ] `Runtime.Host.CompositionRoot.ConfigureServices` — register `AddAgentPlugins(options.PluginsDirectory ?? "/var/lib/vais/plugins")` when the directory exists or an env var is set.
- [ ] `RuntimeOptions` — add `string? PluginsDirectory` + env var `VAIS_PLUGINS_DIRECTORY` with `/var/lib/vais/plugins` default.
- [ ] `appsettings.json` — document the `Plugins.Directory` knob (equivalent to the env var).
- [ ] Dockerfile — no change needed; the VOLUME + chown already exist from Pillar A.
- [ ] `samples/PluginAgentWeather/` — single-file consumer-authored `IAiAgent` implementation + assembly-level `[VaisPlugin]` attribute + `Dockerfile.overlay` that does `COPY --from=publish /plugins /plugins` on top of `vais-agents-runtime:0.18.0-preview`.
- [ ] `samples/PluginAgentWeather/README.md` — step-by-step build + docker compose overlay.
- [ ] Integration test under `tests/Vais.Agents.Runtime.Host.Tests/PluginLoadingIntegrationTests.cs` — publishes the sample into a temp dir, runs composition root, asserts plugin loads + `AgentManifestTranslator` produces options with `Agent` set. Skip TestCluster.

### PR 4 — Docs + tag

- [ ] `docs/concepts/runtime-plugins.md` — design + discovery + isolation model + URN catalogue.
- [ ] `docs/guides/package-an-agent-as-a-plugin.md` — end-to-end walkthrough using the sample.
- [ ] `docs/reference/packages.md` — new row for `Vais.Agents.Runtime.Plugins`. Version bump to `0.18.0-preview`. 26 → 27 packages.
- [ ] `docs/concepts/architecture.md` — add "Plugin tier (v0.18 Pillar C)" section.
- [ ] `docs/concepts/declarative-agents.md` — update the `handler.typeName` coexistence table to reflect plugin-wins semantic.
- [ ] `docs/index.md` — new concept + guide entries.
- [ ] `PublicAPI.Shipped.txt` promotion in `Abstractions` + `Core` + `Hosting.Orleans` + new `Runtime.Plugins`.
- [ ] Milestone entry in `plans/actor-agents-oss-milestone-log.md`.
- [ ] Tick Pillar C in `plans/actor-agents-oss-phase-3-runtime-productisation.md`.
- [ ] Tag `v0.18.0-preview` on the merge commit (awaiting user confirmation — same protocol as v0.16 / v0.17).

Sizing: PR 1 ≈ 3 days, PR 2 ≈ 1-2 days, PR 3 ≈ 2-3 days, PR 4 ≈ 1-2 days. **Total 7-10 working days.** Matches master-plan sizing.

---

## Flagged risks + open items

- **`AssemblyLoadContext` shared-types carve-out must cover transitives.** If a plugin's factory takes `ILogger<T>` from its own context but the runtime hands back an `ILogger<T>` from its own context, the types don't match. Mitigation: the shared list MUST include every DI-boundary type, which means `Microsoft.Extensions.DependencyInjection.Abstractions` + `Microsoft.Extensions.Logging.Abstractions` + `Microsoft.Extensions.Hosting.Abstractions` + `Polly` + every `Vais.Agents.*` assembly. Test with a plugin that explicitly references a mismatched version to confirm.
- **Collectible vs. non-collectible.** Non-collectible wins for v0.18 (no hot-reload), but plugins leak forever if the runtime reuses process space. Acceptable — the runtime lifetime is bounded by the container.
- **Factory creation is per grain activation.** If activation is frequent (grain eviction + reactivation on each invoke), the factory runs per invoke. Usually cheap, but if a plugin does heavy init (SDK clients, big model loads), it repeats. Document the pattern: plugins that cache expensive state should hold DI-scoped singletons, not per-activation state.
- **Plugin throws in `CreateAsync`.** Surfaces as `urn:vais-agents:plugin-factory-throw` with the exception type in details. Partners using WireMock-style tests can reproduce; production hosts log the full stack.
- **ABI version parsing ambiguity.** Attribute says `TargetApiVersion = "0.18"` — do we match "0.18.0" against "0.18"? **Decide in findings**: major-version-only. Plugin built against 0.18 loads on any 0.18.x; fails to load on 0.19.x. Partners bump when breaking changes land.
- **Plugin + manifest-validator feedback.** `vais apply -f manifest.yaml` where the `TypeName` references an unknown plugin — should the runtime fail apply (fail-fast) or accept apply + 404 on invoke (lazy)? **Lean:** fail-apply — partner gets immediate feedback at deploy time instead of silent drift. Findings doc locks this.
- **Startup vs. first-invoke loading.** If the plugins directory has 50 DLLs, startup slows. Mitigation: scan happens once per silo boot; readiness probe still gates on `Orleans.Active`. If scan itself blocks silo activation, add a timeout + continue-on-error with loaded-count / failed-count in the startup log.
- **Non-.NET plugins.** Deferred; partners use A2A cross-runtime refs (Pillar E v0.20) or the `/v1/agents/{id}/invoke` HTTP surface from another process.

---

## What the findings doc will commit to

Rough sketch of the decisions the spike leans toward:

| # | Decision |
|---|---|
| 1 | New `Vais.Agents.Runtime.Plugins` library package (IsPackable=true). |
| 2 | `IAgentHandlerFactory` is the primary contract; `IAiAgent` direct-impl supported via trivial auto-wrap factory. |
| 3 | `[assembly: VaisPluginAttribute]` — no `plugin.json` sidecar. |
| 4 | Per-plugin `PluginAssemblyLoadContext` (non-collectible) with explicit shared-types carve-out covering `Vais.Agents.Abstractions` + DI / logging / hosting abstractions + MEAI. |
| 5 | Per-plugin subfolder layout — scan `/var/lib/vais/plugins/*/`. |
| 6 | `StatefulAgentOptions.Agent` slot — translator stashes, grain prefers over `StatefulAiAgent` construction. |
| 7 | Plugin wins when both set; WARN at apply time. |
| 8 | Startup-log introspection for v0.18; `/v1/plugins` endpoint is v0.18.x polish. |
| 9 | Five new URNs wired into Problem Details. |
| 10 | Full `IServiceProvider` + documented contract-surface; no restricted API. |

Timeline: findings doc next, then pillar plan, then PR 1 kicks off.
