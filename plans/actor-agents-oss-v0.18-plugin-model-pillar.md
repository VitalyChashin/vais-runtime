# v0.18.0-preview — Plugin model for code-authored agents pillar

Tactical plan for [Phase 3 Pillar C](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-c--plugin-model-for-code-authored-agents-us-2-primary) — let partners drop a .NET DLL with a custom `IAiAgent` into `/var/lib/vais/plugins/` and have the runtime host it without rebuilding the container. Grounded in the spike + findings: [`actor-agents-oss-v0.18-plugin-model-{spike,findings}.md`](./actor-agents-oss-v0.18-plugin-model-spike.md). Parallel shape to [`actor-agents-oss-v0.17-manifest-instantiation-pillar.md`](./actor-agents-oss-v0.17-manifest-instantiation-pillar.md). Created 2026-04-21.

---

## Scope

**MVP boundary locked 2026-04-21** via the research spike + findings (user confirmed leans: "Proceed to findings and plan"). 10 decisions:

1. **New package** `Vais.Agents.Runtime.Plugins` (library-layer, `IsPackable=true`). Holds loader + `AssemblyLoadContext` + handler registry. `System.Runtime.Loader` dependency; no Orleans.
2. **Primary contract `IAgentHandlerFactory`** (in `Abstractions`) — plugin authors implement this. Ships in PR 1 alongside a `DefaultHandlerFactory<T>` auto-wrap the loader synthesises for `IAiAgent`-direct impls.
3. **`[assembly: VaisPluginAttribute(targetApiVersion, handlers[])]`** as the source-of-truth descriptor. Convention-based fallback scans `IAgentHandlerFactory` implementations when no attribute.
4. **Per-plugin `PluginAssemblyLoadContext`** (non-collectible). Shared-types carve-out covers `Vais.Agents.*` ABI + DI / hosting / logging / options / MEAI / Polly.
5. **Per-plugin subfolder** layout — `/var/lib/vais/plugins/<name>/<name>.dll` + transitive deps from `dotnet publish`. `AssemblyDependencyResolver` handles the rest.
6. **`StatefulAgentOptions.Agent`** init-only slot — translator stashes plugin-produced `IAiAgent`; grain prefers it over constructing `StatefulAiAgent`. Pillar B's shape extended.
7. **Plugin wins + WARN** when both `Model` + matching `TypeName` set. Apply response carries warnings; CLI prints `warn:` to stderr.
8. **Startup-log introspection** for v0.18 (`/v1/plugins` endpoint + CLI command defer to v0.18.x).
9. **Six new URNs** in `ManifestInstantiationUrns` (`plugin-load-failed`, `plugin-abi-mismatch`, `plugin-handler-collision`, `plugin-handler-not-found`, `plugin-factory-throw`, `handler-and-declarative-fields-both-set`).
10. **Full `IServiceProvider`** access from factories + documented contract surface (`ISecretResolver`, `IHttpClientFactory`, `ILogger<T>`, `IAgentRegistry`, `IAgentContextAccessor`, `TimeProvider`).

### Semantic projection chosen

**Code-as-another-manifest-shape.** The `AgentManifest` already has two valid shapes after v0.17: declarative (`Model != null`) or unknown (`Model == null` → 501). Pillar C adds a third — code-bound (`Handler.TypeName` matches a loaded plugin). Same manifest contract, same HTTP verbs, three translation paths, one invocation surface.

### Explicitly deferred to post-v0.18

- **Sidecar-container plugins** — non-.NET hosting. Phase 4 open question; A2A cross-runtime refs (Pillar E) cover most cases.
- **Hot reload** — master-plan non-goal; plugins load at silo start.
- **`/v1/plugins` HTTP endpoint + `vais plugins list` CLI** — v0.18.x polish. Startup log covers v0.18.
- **Strict apply-time validation** of `TypeName` presence — v0.18.x polish. v0.18 accepts manifests with pending plugins (matches deploy-plugin-later flow); invoke hits `handler-not-loaded` until loaded.
- **Cross-ABI compat** — plugins declare `TargetApiVersion`; loader enforces major match. No version shims.
- **Plugin signing / SBOM / supply-chain verification** — Pillar F polish.
- **Plugin capability negotiation** — beyond `IStreamingAiAgent` inheritance check, no "optional capabilities" advertisement.

---

## Design questions — resolved

Full table + evidence in [`actor-agents-oss-v0.18-plugin-model-findings.md`](./actor-agents-oss-v0.18-plugin-model-findings.md). Summary:

| # | Question | Decision |
|---|---|---|
| 1 | Loader code location | New `Vais.Agents.Runtime.Plugins` library package |
| 2 | Plugin author contract | `IAgentHandlerFactory` primary; auto-wrap for `IAiAgent`-direct impls |
| 3 | Descriptor format | `[assembly: VaisPluginAttribute]` + convention fallback |
| 4 | Isolation | Per-plugin non-collectible `PluginAssemblyLoadContext` + shared-types carve-out |
| 5 | Directory layout | Per-plugin subfolder; `AssemblyDependencyResolver` for transitives |
| 6 | Grain-seam integration | `StatefulAgentOptions.Agent` slot; grain prefers over `StatefulAiAgent` construction |
| 7 | `Model` + `TypeName` coexistence | Plugin wins; WARN at apply time |
| 8 | Discovery flow | Startup log in v0.18; `/v1/plugins` in v0.18.x |
| 9 | URN catalogue | Six new URNs in `ManifestInstantiationUrns` |
| 10 | DI surface | Full `IServiceProvider` + documented contract surface |

---

## Proposed PR shape

Four-PR sequence inside `v0.18`. Each independently shippable.

### PR 1 — `Vais.Agents.Runtime.Plugins` package + contracts + loader ✅

- [x] `IAgentHandlerFactory` contract in **`Vais.Agents.Abstractions`** — `HandlerTypeName` + `CreateAsync`. PublicAPI-Unshipped.
- [x] `VaisPluginAttribute` (assembly-level, `Vais.Agents.Abstractions`) — `TargetApiVersion: string`, `Handlers: IReadOnlyList<string>`, validates non-empty entries.
- [x] `StatefulAgentOptions.Agent` init-only slot in **`Vais.Agents.Core`** — additive; mirrors Pillar B's `CompletionProvider` slot.
- [x] `src/Vais.Agents.Runtime.Plugins/` library project — `Microsoft.NET.Sdk`, `net9.0`, `IsPackable=true`, PublicAPI analyzer on.
- [x] `PluginAssemblyLoadContext : AssemblyLoadContext` — non-collectible; shared-types carve-out covering the 10 assemblies in findings §Q4; `AssemblyDependencyResolver` handles managed + unmanaged transitives.
- [x] `PluginDescriptor` record — Name + AssemblyPath + TargetApiVersion + Handlers + LoadedViaAttribute + LoadContext.
- [x] `IPluginHandlerRegistry` + internal `PluginHandlerRegistry` — ordinal-case-sensitive lookup; concurrent for thread-safety.
- [x] `DefaultHandlerFactory<T>` + non-generic `Create(Type, string)` helper using `ActivatorUtilities.CreateInstance<T>`.
- [x] `AssemblyPluginLoader` — scan + primary-assembly pick (`<folder>.dll` preferred + single-DLL fallback) + per-plugin context + attribute / convention discovery + ABI match (major+minor for 0.x) + structured INFO/WARN logs per findings §Q8.
- [x] `PluginLoadException` — Urn + PluginPath (two non-ambiguous ctors: required pluginPath to satisfy RS0026 backcompat analyzer).
- [x] `PluginUrns` static catalog (5 URNs) + `PluginLoaderOptions` (RuntimeAbiVersion + AllowConventionDiscovery + FailOnHandlerCollision) + `VaisRuntimeAbi.CurrentVersion = "0.18"`.
- [x] `AddAgentPlugins(IServiceCollection, string, PluginLoaderOptions?)` DI extension — lazy-resolves loader so `ILogger<AssemblyPluginLoader>` is available.
- [x] 23 unit tests covering:
  - Flat-directory scan finds zero plugins (empty dir).
  - One plugin with `VaisPluginAttribute` loads + registers per-handler factories.
  - Plugin without `VaisPluginAttribute` falls through to convention scan + loads factories by `IAgentHandlerFactory` impl.
  - Convention scan emits warning log when no attribute.
  - Plugin `TargetApiVersion = "0.17"` on a 0.18 runtime → not loaded, log-warn, URN `plugin-abi-mismatch`.
  - Two plugins both exporting same `TypeName` → startup throws with URN `plugin-handler-collision`.
  - Plugin DLL that can't be loaded (bad IL / missing dep) → log-warn URN `plugin-load-failed`, other plugins still load.
  - Plugin that exports `IAgentHandlerFactory` but the type throws in static ctor → log-warn.
  - Per-plugin `AssemblyLoadContext` isolates plugin deps: two plugins importing different `Newtonsoft.Json` versions both load.
  - Shared-type boundary: plugin's `IAiAgent` resolves to runtime's assembly (no InvalidCastException).
  - `DefaultHandlerFactory<T>` auto-wraps a trivial `IAiAgent` impl + `CreateAsync` returns instance via `ActivatorUtilities`.
  - `IPluginHandlerRegistry.TryGet` before + after load returns expected results.
  - `StatefulAgentOptions.Agent` slot is init-only + default null.
  - `AssemblyPluginLoader` idempotent across repeated calls (doesn't re-load).
- [x] PublicAPI.Unshipped populated (new package + Abstractions additions + Core addition).
- [x] Full solution builds clean; 0 warnings, 0 errors; 23 new tests green; 21 test projects total all green.

### PR 2 — Translator + grain wiring + URN catalog

- [x] `AgentManifestTranslator.TranslateAsync` — new branch: **before** the `Model is null` check, query `IPluginHandlerRegistry.TryGet(manifest.Handler.TypeName)`. If factory found, `await factory.CreateAsync(manifest, sp, ct)` + stash result in `StatefulAgentOptions.Agent`. If `Model` ALSO set, record a diagnostic warning for the apply response + continue with plugin.
- [x] If no plugin match + `Model` set → existing declarative path (Pillar B).
- [x] If no plugin match + no `Model` → existing `handler-not-loaded` URN (Pillar B) — message updated to reference the missing-plugin-handler case.
- [x] `AiAgentGrain.OnActivateAsync` — prefer `supplied.Agent` over constructing `StatefulAiAgent`. Field type widened to `IAiAgent?`; persisted SystemPrompt is re-applied to plugin-supplied agents via the existing `IAiAgent.SystemPrompt` setter. Plugin-owned history rehydration is a factory concern (v0.19+).
- [x] Two new URN constants in `ManifestInstantiationUrns` (`plugin-factory-throw`, `handler-and-declarative-fields-both-set`); remaining four (loader-side: `plugin-load-failed`, `plugin-abi-mismatch`, `plugin-handler-collision`, `plugin-handler-not-found`) shipped with PR 1 under `PluginUrns`.
- [x] `IManifestApplyDiagnosticsSink` contract (in `Control.Abstractions`) — lightweight sink the translator calls when emitting apply-time warnings. Optional DI; absence = sink not invoked.
- [x] `ManifestInstantiationException` branch covers `plugin-factory-throw` — wraps the factory's thrown exception, URN + inner-exception type surface via `ex.InnerException`. `OperationCanceledException` propagates unwrapped.
- [x] Unit tests — `tests/Vais.Agents.Runtime.Instantiation.Tests/PluginTranslationTests.cs` (13 tests, all green):
  - Plugin match returns `options.Agent` from factory + no `CompletionProvider` set.
  - Plugin match cached across calls (first-writer-wins).
  - No plugin match + `Model` set → declarative path unchanged (`CompletionProvider` populated, `Agent` null).
  - Plugin match + `Model` set → plugin wins; declarative `SystemPrompt`/`CompletionProvider` ignored.
  - Plugin + declarative both set → diagnostics sink records `handler-and-declarative-fields-both-set`.
  - Plugin match alone → sink NOT invoked.
  - Plugin factory throws → `plugin-factory-throw` URN + inner exception preserved.
  - Plugin factory throw does NOT cache a partial result (retry re-invokes the factory).
  - No plugin registry + null `Model` → `handler-not-loaded` URN message references the handler TypeName.
  - Plugin registry present but no matching handler + null `Model` → `handler-not-loaded`.
  - Plugin factory receives the manifest + host `IServiceProvider` verbatim.
  - Plugin match preserves `manifest.Budget` on the returned options.
  - Cancellation propagates as `OperationCanceledException` (not wrapped).
- [x] Full OSS solution builds clean; 0 warnings / 0 errors; every test project green including 59 Runtime.Instantiation.Tests (46 pre-existing + 13 new), 23 Runtime.Plugins.Tests, 66 Hosting.Orleans.Tests.

### PR 3 — Runtime host wiring + sample + integration test

- [x] `RuntimeOptions` (in `Runtime.Host`) — added `string? PluginsDirectory` with default `RuntimeOptions.DefaultPluginsDirectory` = `"/var/lib/vais/plugins"`. `FromEnvironment()` reads `VAIS_PLUGINS_DIRECTORY`; unset = default, explicit empty string = disabled (loader skipped, no registry wired).
- [x] `CompositionRoot.ConfigureServices` — registers `AddAgentPlugins(options.PluginsDirectory)` before `AddAgentManifestInstantiator` so the translator picks up `IPluginHandlerRegistry` at build time. Skipped entirely when `PluginsDirectory` is null/whitespace (disabled mode). Guarded by three unit tests: `Composition_Plugin_Registry_Registered_When_PluginsDirectory_Set`, `Composition_Plugin_Registry_Not_Registered_When_PluginsDirectory_Empty`, `Composition_Plugin_Registry_Registered_Before_Translator`.
- [x] `appsettings.json` — `Plugins:Directory` key documented alongside a `_Comment_Plugins` note that `VAIS_PLUGINS_DIRECTORY` is the override.
- [x] `samples/PluginAgentWeather/` — sample project:
  - `MyApp.WeatherAgent/MyApp.WeatherAgent.csproj` — `net9.0`, Library output, references `Vais.Agents.Abstractions` + `Vais.Agents.Core` via preview `0.18.0-preview` NuGet (consistent with other samples' external-consumer posture), sets `CopyLocalLockFileAssemblies=true` + `SelfContained=false`.
  - `MyApp.WeatherAgent/WeatherAgent.cs` — `public sealed class WeatherAgent : IAiAgent` with a hardcoded `"Sunny!"` reply, `InMemoryAgentSession`-backed history, and no outbound HTTP.
  - `MyApp.WeatherAgent/AssemblyInfo.cs` — `[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent")]`.
  - `Dockerfile.overlay` — `FROM ghcr.io/vais-agents/runtime:0.18.0-preview` + `COPY ./publish /var/lib/vais/plugins/weather-agent`. Shows both overlay-image and bind-mount patterns.
  - `README.md` — build + publish + overlay/docker-mount + `vais apply` + `vais invoke` walk-through, plus the `VAIS_PLUGINS_DIRECTORY=""` disable hint.
- [x] Integration test `tests/Vais.Agents.Runtime.Host.Tests/PluginLoadingIntegrationTests.cs` — 2 tests:
  - `Runtime_Loads_Plugin_And_Translator_Returns_Plugin_Agent`: stages a sibling fixture assembly (`Vais.Agents.Runtime.Host.PluginFixture`, ProjectReferenced with `ReferenceOutputAssembly=false` so it builds as a standalone plugin DLL but isn't linked into the test assembly), copies the fixture bin into a temp plugins dir, boots the composition root against it, registers a manifest, asserts the translator's plugin branch populates `options.Agent` with the fixture's `WeatherAgent`, and end-to-end verifies `AskAsync("hello") == "Sunny!"`.
  - `Runtime_With_Empty_PluginsDirectory_Skips_Loader`: sets `PluginsDirectory=""`, asserts `IPluginHandlerRegistry` is not registered, and that a manifest with a plugin-shaped `TypeName` surfaces `handler-not-loaded`.
  - Integration tests use the new `Vais.Agents.Runtime.Host.PluginFixture` csproj (registered in the solution) as the plugin assembly under test. A post-hoc `RemoveAll<IAgentRegistry>()` + `AddSingleton<InMemoryAgentRegistry>` swap replaces the Orleans registry so the test doesn't need a live silo.
- [x] `samples/README.md` — pointer row added for `PluginAgentWeather`.
- [x] Full OSS solution builds clean; 0 warnings / 0 errors; every test project green including 20 Runtime.Host.Tests (12 pre-existing + 6 composition-root + 2 integration) alongside 59 Runtime.Instantiation.Tests and 23 Runtime.Plugins.Tests.

### PR 4 — Docs + tag

- [x] `docs/concepts/runtime-plugins.md` — new. Loader design + isolation model + `VaisPluginAttribute` semantics + ABI-version matching rules + `IAgentHandlerFactory` contract + lifecycle + full URN catalogue + security posture note.
- [x] `docs/guides/package-an-agent-as-a-plugin.md` — new. 9-step walkthrough from `dotnet new classlib` through overlay-image publish + `vais apply` + `vais invoke` + troubleshooting.
- [x] `docs/concepts/declarative-agents.md` — rewrote `handler.typeName` coexistence table to "plugin wins + WARN" semantics; added v0.18 URNs (`plugin-factory-throw`, `handler-and-declarative-fields-both-set`) to the failure table; clarified the `declarative` sentinel convention.
- [x] `docs/concepts/architecture.md` — new "Plugin tier (v0.18 Pillar C)" section with ASCII diagram; Runtime.Plugins node added to the mermaid layering; "25 → 27 packages" bump; library-tier runtime diagram updated.
- [x] `docs/reference/packages.md` — version bump to `0.18.0-preview`; 26 → 27 packages; Plugin model (v0.18) section with Runtime.Plugins row; new "Plugin-authored agent shipped as a DLL" scenario bundle.
- [x] `docs/reference/runtime-configuration.md` — new Plugin loader section with `VAIS_PLUGINS_DIRECTORY` env var + `Plugins:Directory` appsettings key + disable guidance + startup-log grep hints. Composition-root-baked decisions updated to reflect v0.17 `OrleansAgentRegistry` swap + v0.18 loader ordering.
- [x] `docs/reference/problem-details-urns.md` — regrouped into three sections (core / v0.17 manifest instantiation / v0.18 plugin model); added 2 runtime URNs + 4 startup-log-only plugin URNs with caller-response guidance.
- [x] `docs/index.md` — new runtime-plugins concept row, package-an-agent-as-a-plugin guide row, 27-package reference line, plugin-authoring quick-map entry.
- [x] `docs/getting-started/installation.md` — 25 → 27 packages (caught v0.17-era stale wording).
- [x] `PublicAPI.Shipped.txt` promotion across Abstractions (7 entries: `IAgentHandlerFactory` + `VaisPluginAttribute`), Control.Abstractions (2 entries: `IManifestApplyDiagnosticsSink`), Core (2 entries: `StatefulAgentOptions.Agent`), Runtime.Instantiation (2 URN consts), Runtime.Plugins (52 entries — whole new package promoted). Hosting.Orleans had no Unshipped additions (grain activation tweak was an internal field/method retype).
- [x] Milestone entry appended to `plans/actor-agents-oss-milestone-log.md` — follows v0.16 / v0.17 shape.
- [x] Pillar C ticked in `plans/actor-agents-oss-phase-3-runtime-productisation.md`.
- [x] Tag `v0.18.0-preview` — annotated on OSS commit `454ec33` (2026-04-21). Two-commit bundle on OSS `main`: `464a8b6` (library layer) + `454ec33` (runtime host + sample + docs).

Sizing: PR 1 ≈ 2-3 days, PR 2 ≈ 1-2 days, PR 3 ≈ 2-3 days, PR 4 ≈ 1-2 days. **Total 7-10 working days** (~2 weeks). Matches master plan sizing.

---

## Acceptance

Pillar C is done when:

- [ ] `samples/PluginAgentWeather` publishes + is dropped into `/var/lib/vais/plugins/weather-agent/` (via docker overlay or PVC mount); partner runs `vais apply -f weather-agent.yaml` (with `handler.typeName: MyApp.WeatherAgent`) + `vais invoke weather --text "hello"` → returns `"Sunny!"` (or whatever the sample's agent produces). No 501.
- [ ] Apply with `handler.typeName` pointing at an unknown plugin → accepts (v0.18 default; strict mode polish for v0.18.x); invoke hits `501 urn:vais-agents:handler-not-loaded` with message pointing at plugin-not-loaded.
- [ ] Apply with both `Model` + `handler.typeName` pointing at loaded plugin → 200 with `warnings: ["handler-and-declarative-fields-both-set"]`; invoke uses plugin.
- [ ] Two plugins exporting the same `TypeName` → runtime startup fails with `urn:vais-agents:plugin-handler-collision` + structured log listing both plugin paths.
- [ ] Plugin with `[assembly: VaisPlugin(targetApiVersion: "0.17", ...)]` on a 0.18 runtime → log-warn `urn:vais-agents:plugin-abi-mismatch`; runtime starts successfully with that plugin excluded.
- [ ] Plugin's factory throws in `CreateAsync` → invoke returns `500 urn:vais-agents:plugin-factory-throw` with inner exception type in Problem Details extensions.
- [ ] Per-plugin isolation: two plugins, each with its own Newtonsoft.Json version, both load + work.
- [ ] Composition-root unit tests include plugin-registry-ordering guard (`Composition_Plugin_Registry_Registered_Before_Translator`).
- [ ] Full Pillar A (7) + Pillar B (10) composition-root guards + Pillar C new guards stay green.
- [ ] 46 Runtime.Instantiation tests + 15+ Runtime.Plugins tests + 10+ plugin-integration tests all green.
- [ ] Build clean; 0 warnings.
- [ ] Docs reviewed; cross-links intact from `index.md` / `architecture.md` / `packages.md` / `declarative-agents.md`.
- [ ] Tag `v0.18.0-preview` created.

---

## Composition-root extension — sketch

Reference for PR 3. New registrations marked `// NEW in v0.18`; Pillar A + Pillar B wiring compressed to ellipsis.

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ...Pillar A (durability sidecars) + Pillar B (Orleans registry + translator + providers + guardrails)...

    // ── v0.18 Pillar C: plugin loader BEFORE the translator so translator
    //    can query the handler registry at translate time. AddAgentPlugins is
    //    a no-op when the directory is null or doesn't exist.
    if (!string.IsNullOrWhiteSpace(options.PluginsDirectory) && Directory.Exists(options.PluginsDirectory))
    {
        services.AddAgentPlugins(options.PluginsDirectory);                  // NEW in v0.18
    }

    services.AddAgentManifestInstantiator();
    services.AddBuiltinModelProviders();
    services.AddBuiltinGuardrails();

    services.ConfigureAgentGrains((sp, id) =>
        sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));

    // ...rest of Pillar A + B unchanged...
}
```

Consumer surface — partner with a custom host opts in with three lines beyond v0.17:

```csharp
builder.Services.AddAgentManifestInstantiator();
builder.Services.AddBuiltinModelProviders();
builder.Services.AddBuiltinGuardrails();
builder.Services.AddAgentPlugins("/var/lib/vais/plugins");                   // NEW in v0.18
builder.Services.ConfigureAgentGrains((sp, id) =>
    sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));
```

---

## Sample `IAiAgent` plugin shape — sketch

Reference for PR 3. The partner's assembly looks like:

```csharp
// MyApp.WeatherAgent/AssemblyInfo.cs
[assembly: Vais.Agents.VaisPlugin(
    targetApiVersion: "0.18",
    "MyApp.WeatherAgent")]

// MyApp.WeatherAgent/WeatherAgent.cs
namespace MyApp;

public sealed class WeatherAgent(IHttpClientFactory httpFactory, ILogger<WeatherAgent> logger) : IAiAgent
{
    public string? SystemPrompt { get; set; }
    public IAgentSession? Session { get; set; }
    public IReadOnlyList<ChatTurn> History => Array.Empty<ChatTurn>();

    public async Task<string> AskAsync(string userMessage, CancellationToken ct = default)
    {
        logger.LogInformation("Weather ask: {msg}", userMessage);
        // Realistic implementation: call a weather API via httpFactory...
        return "Sunny!";
    }

    public void Reset() { /* nothing to reset */ }
}
```

No plugin factory required — the loader auto-wraps via `DefaultHandlerFactory<WeatherAgent>` because `WeatherAgent` implements `IAiAgent` + has a DI-friendly ctor. Partners who want access to `AgentManifest` at construction time ship their own `IAgentHandlerFactory` instead:

```csharp
public sealed class WeatherAgentFactory(IHttpClientFactory httpFactory, ILogger<WeatherAgent> logger) : IAgentHandlerFactory
{
    public string HandlerTypeName => "MyApp.WeatherAgent";

    public ValueTask<IAiAgent> CreateAsync(AgentManifest manifest, IServiceProvider sp, CancellationToken ct)
    {
        // Custom: inspect manifest.Annotations for per-agent config.
        return new(new WeatherAgent(httpFactory, logger) { SystemPrompt = manifest.SystemPrompt?.Inline });
    }
}
```

Both shapes ship in the same plugin package; factory wins when both present for the same TypeName.

---

## Timeline

- Spike + findings: complete.
- PR 1 (package + loader + tests): 2-3 days.
- PR 2 (translator + grain wiring + URNs): 1-2 days.
- PR 3 (host wiring + sample + integration test): 2-3 days.
- PR 4 (docs + tag): 1-2 days.

Total Pillar C: **7-10 working days** (~2 weeks). Matches master-plan sizing.

---

## Risks + mitigations

- **Shared-types list incompleteness.** Every DI boundary type crossing the plugin-runtime seam must be in the carve-out. If a future Pillar F adds (e.g.) `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` as a DI-crossing type, the carve-out needs an update. **Mitigation**: findings-doc amendment process; unit test per boundary type that verifies `InvalidCastException` doesn't fire when a plugin loads an adjacent-version carve-out assembly.
- **`AssemblyLoadContext` performance on startup.** Each plugin = a new load context = a new assembly load per transitive. 20 plugins × 10 deps = 200 loads. Typical overhead ~5-10 ms each = ~1-2 s startup bump. **Mitigation**: document as known-cost; parallel load is a polish item.
- **Factory invocation cost per grain activation.** Plugin author may ship an expensive-to-construct agent. **Mitigation**: document the "cache in DI-scoped singleton" pattern; runtime log at DEBUG per factory invocation to make the cost visible.
- **Plugin leaks forever.** Non-collectible = plugin assemblies never unload. **Mitigation**: acceptable for v0.18 (container-lifetime); re-evaluate when Phase 4 considers hot-reload.
- **ABI-version drift between 0.17 and 0.18.** Pillar B shipped several Abstractions additions (A2ARemoteAgentRef, AgentManifest.A2ARemoteAgents, StatefulAgentOptions.CompletionProvider, IAgentManifestInvalidator). Partners who built plugins against "0.17" (hypothetical, since plugins didn't exist in 0.17) would now hit abi-mismatch. **Mitigation**: plugins only exist starting v0.18; `TargetApiVersion = "0.18"` is the floor. Document.
- **Symbol-collision between plugins.** Two plugins exporting the same .NET type name (not `TypeName`-string but actual CLR type name) work fine — different `AssemblyLoadContext`s — but two plugins exporting the same *handler TypeName* string fail fast. **Mitigation**: documented in the URN catalog + startup error is actionable ("remove one of plugins/{name-a} / plugins/{name-b} that both export MyApp.Foo").
- **Plugin-produced agent doesn't wire to the grain's state hydration.** If the plugin's `IAiAgent` doesn't expose a settable `SystemPrompt` or has read-only `History`, the grain can't restore persisted state. **Mitigation**: `ApplyPersistedState` is best-effort — it sets `SystemPrompt` via the interface setter when non-null; skips History (plugins handle their own if they care). Document the contract: "Implement `IAiAgent` with settable SystemPrompt if you want grain-state hydration."
- **Sample publishing pipeline + overlay-image size.** The sample's `Dockerfile.overlay` copies `dotnet publish` output; a bad partner config could balloon the image. **Mitigation**: sample README calls out `SelfContained=false` + `PublishSingleFile=false`; `.dockerignore` patterns provided.

---

## Progress log

- 2026-04-21 — Pillar plan created. Scope locked from spike + findings. Four-PR sequence: package + loader → translator + grain wiring → host wiring + sample + integration test → docs + tag. ~7-10 working days. **Pending**: PR 1.
- 2026-04-21 — PR 1 landed. Added `src/Vais.Agents.Runtime.Plugins/` (9 source files + PublicAPI) with `IPluginHandlerRegistry` / `PluginAssemblyLoadContext` / `DefaultHandlerFactory` / `AssemblyPluginLoader` / `PluginServiceCollectionExtensions` / `PluginLoaderOptions` / `VaisRuntimeAbi` / `PluginUrns` / `PluginLoadException` + `PluginDescriptor`. Added `IAgentHandlerFactory` + `VaisPluginAttribute` to `Abstractions`; added `StatefulAgentOptions.Agent` slot to `Core`. 23 unit tests (well past 15+ target) covering registry collision + auto-wrap DI resolution + attribute validation + load-context shared-types lockdown + loader missing/empty/garbage-dir scenarios + DI-extension null/empty guards. Full solution 0 warnings / 0 errors across 21 test projects. Three drifts from findings worth noting: (1) ABI matching is major+minor, not major-only — during 0.x we need minor to matter because Abstractions moves per release. (2) `PluginLoadException` had to drop optional `pluginPath = null` defaults; RS0026 backcompat analyzer rejects multiple ctors with optional params — now two non-ambiguous ctors both with required pluginPath. (3) `Microsoft.Extensions.Logging` (not just `.Abstractions`) added to `Directory.Packages.props` so tests can `AddLogging()`. **Next**: PR 2 — translator queries plugin registry + adds six new URN constants in `ManifestInstantiationUrns` + `AiAgentGrain` prefers `options.Agent` over constructing `StatefulAiAgent`.
