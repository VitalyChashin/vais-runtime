# v0.22.0-preview — Dynamic plugin hot-reload pillar

Tactical plan for v0.22 — enable plugin DLLs to be swapped in `/var/lib/vais/plugins/` at runtime without restarting the container or silo. Extends v0.18 Pillar C (plugin model). No confirmed partner use case yet — this is a design-exploration milestone; implementation proceeds to "implementation-ready" but ships behind a feature flag so it can be cherry-picked when a partner confirms the need. Created 2026-04-24.

---

## Scope

**MVP boundary locked 2026-04-24** via codebase audit of `Vais.Agents.Runtime.Plugins`. 10 decisions:

1. **Drain-and-swap** reload model only. No live/parallel serving — the old plugin continues serving in-flight requests until all active grain calls drain, then the registry atomically swaps to the new ALC. No concurrent old+new handler versions in-flight.
2. **Make `PluginAssemblyLoadContext` collectible** (`isCollectible: true`). This is the prerequisite for GC reclaiming the old ALC after swap. Currently `isCollectible: false` (v0.18 punt with explicit note in risks). Change is non-breaking at the API surface.
3. **`IHostedService` watcher** (`PluginWatcherService`) owns the `FileSystemWatcher` per configured plugins directory. Monitors `*.dll` creates/changes/renames in direct plugin subfolders. Throttled (200 ms debounce) to survive `dotnet publish` multi-file writes.
4. **`IPluginReloader`** new interface (in `Runtime.Plugins`) — single method `ReloadAsync(string pluginPath, CancellationToken)`. Injected into `PluginWatcherService`; testable independently of the filesystem watcher.
5. **`PluginHandlerRegistry.SwapAsync`** — atomic swap: load new plugin → acquire a write-lock → replace entries for affected handlers → release lock → return the old `PluginDescriptor` for drain+deactivation.
6. **Orleans grain deactivation** — after swap, call `IGrainManagementExtension.DeactivateAsync(DeactivationOptions.Graceful)` on each grain whose `HandlerTypeName` matched the old descriptor. Grains are enumerated via `IManagementGrain.GetDetailedStatistics()` filtered by handler type. Deactivated grains re-activate against the new registry on next invoke.
7. **`ReloadPolicy`** enum on `PluginLoaderOptions`: `Disabled` (default, v0.18 behaviour) | `DrainAndSwap`. Feature flag preserves backward compatibility.
8. **Structured reload events** — six new log messages (DEBUG/INFO/WARN) for: watcher-started, change-detected, reload-begin, reload-success, reload-failed (old kept), drain-complete.
9. **Collectible ALC leak test** — mandatory correctness gate: `WeakReference.IsAlive` after N forced GC cycles must return `false` once the old `PluginDescriptor.LoadContext` is released. Ships as a unit test in `Runtime.Plugins.Tests`.
10. **Explicit non-goals** (see below) documented in the feature-flag release notes; no shims or migration paths for deferred items.

### Reload model chosen

**Drain-and-swap.** Chosen over live/parallel because: (a) no partner use case for concurrent old/new versions; (b) live/parallel requires handler-version routing at every invoke site, a large blast radius; (c) Orleans graceful deactivation already provides "drain" semantics without new protocol. Trade-off: a brief window between swap and re-activation where grains report `handler-not-loaded` — acceptable for the design-exploration milestone and documented as known behaviour.

### Explicitly deferred to post-v0.22

- **Live/parallel reload** — old+new handler versions served simultaneously via version-tagged manifests. Requires invoke-site routing; no partner use case.
- **State migration on handler-type removal** — if a plugin removes a handler type, grains referencing that TypeName get `handler-not-loaded` permanently. No migration path.
- **Shape-change migration** — if the plugin's `IAiAgent` state format changes between versions (persisted `History`, custom fields), no automatic migration. Operator responsibility.
- **ABI version mismatch on reload** — if new plugin DLL has `TargetApiVersion` that no longer matches runtime, `plugin-abi-mismatch` log-warn is emitted; old descriptor is kept unchanged (no rollback needed — swap never committed).
- **Cross-node drain** — in a multi-silo cluster, deactivation is per-local-silo. Remote silos pick up the new registry on their next activation round. Cross-silo drain coordination deferred.
- **`/v1/plugins/reload` HTTP endpoint** — operator-triggered reload via API. v0.22 is watcher-only; API trigger is v0.22.x polish.
- **Non-`*.dll` asset reload** — only assembly-and-deps reload is in scope. Configuration-file updates within a plugin package require pod restart.

---

## Design questions — resolved

| # | Question | Decision |
|---|---|---|
| 1 | Collectibility | Change `PluginAssemblyLoadContext` to `isCollectible: true`; verify shared-types carve-out still holds (load-context unit tests already cover this) |
| 2 | Watcher placement | New `PluginWatcherService : IHostedService` in `Runtime.Plugins`; registered conditionally when `ReloadPolicy != Disabled` |
| 3 | Reload interface | New `IPluginReloader` in `Runtime.Plugins`; `DefaultPluginReloader` impl; `PluginWatcherService` depends on it |
| 4 | Registry swap | `PluginHandlerRegistry.SwapAsync` — acquires `SemaphoreSlim(1)` write-lock, replaces entries, returns old descriptor |
| 5 | Grain drain | `IManagementGrain.GetDetailedStatistics()` to enumerate affected grains; `IGrainManagementExtension.DeactivateAsync(Graceful)` per grain |
| 6 | Feature flag | `ReloadPolicy` enum on `PluginLoaderOptions`; default `Disabled`; watcher not registered unless `DrainAndSwap` |
| 7 | Debounce | 200 ms debounce on `FileSystemWatcher` events via `CancellationTokenSource`-reset pattern |
| 8 | Leak correctness | `WeakReference<PluginAssemblyLoadContext>` unit test: load → unload → GC × 3 → `!IsAlive` |
| 9 | Rollback | On reload failure (ABI mismatch, load error), old descriptor is kept; new ALC is disposed; `reload-failed` log with URN |
| 10 | Startup vs. watch interaction | `AssemblyPluginLoader.Load()` runs at startup (unchanged). `PluginWatcherService` only watches for changes post-startup. No race — watcher starts after `IHostApplicationLifetime.ApplicationStarted`. |

---

## Proposed PR shape

Four-PR sequence inside `v0.22`. Each independently shippable.

### PR 1 — Collectible ALC + reload API + leak test ✅

- [x] Change `PluginAssemblyLoadContext` constructor: `isCollectible: false` → `isCollectible: true`. Run existing 23 `Runtime.Plugins.Tests` to confirm no regressions (shared-types carve-out still isolates correctly with collectible contexts).
- [ ] Add `IPluginReloader` interface to `Runtime.Plugins`:
  ```csharp
  public interface IPluginReloader
  {
      Task<PluginReloadResult> ReloadAsync(string pluginPath, CancellationToken cancellationToken = default);
  }
  ```
- [ ] Add `PluginReloadResult` record: `PluginDescriptor? OldDescriptor`, `PluginDescriptor? NewDescriptor`, `PluginReloadStatus Status`, `string? FailureUrn`, `Exception? FailureException`.
- [ ] Add `PluginReloadStatus` enum: `Success`, `AbiMismatch`, `LoadFailed`, `NoChange`.
- [ ] Add `ReloadPolicy` enum to `PluginLoaderOptions`: `Disabled = 0` (default), `DrainAndSwap = 1`.
- [ ] Add internal `SwapAsync(string handlerTypeName, PluginDescriptor newDescriptor)` to `PluginHandlerRegistry` backed by `SemaphoreSlim(1,1)` write-lock.
- [ ] Add `DefaultPluginReloader` implementing `IPluginReloader` — loads new ALC via `AssemblyPluginLoader`, validates ABI, calls `SwapAsync`, returns `PluginReloadResult`.
- [ ] Add `PluginReloadUrns` static catalog: `plugin-reload-failed`, `plugin-reload-abi-mismatch`.
- [ ] **Collectible ALC leak test** in `Runtime.Plugins.Tests`:
  ```csharp
  [Fact]
  public void Collectible_Alc_Is_Garbage_Collected_After_Unload()
  {
      var wr = LoadAndUnload();
      for (var i = 0; i < 10; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
      wr.TryGetTarget(out _).Should().BeFalse("collectible ALC must be GC-eligible after all references released");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference<PluginAssemblyLoadContext> LoadAndUnload()
  {
      var ctx = new PluginAssemblyLoadContext("test-plugin", "/tmp/test.dll", isCollectible: true);
      var wr = new WeakReference<PluginAssemblyLoadContext>(ctx);
      ctx.Unload();
      return wr;
  }
  ```
- [ ] `PublicAPI.Unshipped.txt` for `Runtime.Plugins` — new public types: `IPluginReloader`, `PluginReloadResult`, `PluginReloadStatus`, `PluginReloadUrns`, `ReloadPolicy` (on `PluginLoaderOptions`).
- [ ] Full solution builds clean; 0 warnings, 0 errors; all existing tests green + new leak test green.

### PR 2 — FileSystemWatcher service + drain-and-swap + Orleans deactivation

- [ ] `PluginWatcherService : IHostedService` in `Runtime.Plugins`:
  - Creates `FileSystemWatcher` for `PluginLoaderOptions.PluginsDirectory`; filter `*.dll`; watches Created + Changed + Renamed.
  - Per-event debounce: `CancellationTokenSource`-reset pattern with 200 ms delay.
  - Calls `IPluginReloader.ReloadAsync(changedPluginPath, stoppingToken)` on debounce expiry.
  - Emits structured log events: `watcher-started`, `change-detected`, `reload-begin`, `reload-success`, `reload-failed`, `drain-begin`, `drain-complete`.
  - Only registered when `ReloadPolicy == DrainAndSwap` (see DI extension update below).
- [ ] Update `PluginServiceCollectionExtensions.AddAgentPlugins` overload: when `options.ReloadPolicy == DrainAndSwap`, also register `PluginWatcherService` as `IHostedService` and `DefaultPluginReloader` as `IPluginReloader`.
- [ ] `GrainDrainService` (internal, `Runtime.Plugins.Orleans` sub-namespace or `Hosting.Orleans`): after a successful reload, enumerates active grains via `IManagementGrain.GetDetailedStatistics()`, filters by `HandlerTypeName == oldDescriptor.Handlers`, calls `IGrainManagementExtension.DeactivateAsync(new DeactivationOptions { Reason = "plugin-hot-reload" })` per grain. Injected into `DefaultPluginReloader` via `IGrainFactory`.
- [ ] Integration with `ReloadPolicy.Disabled`: `PluginWatcherService` and `GrainDrainService` not registered; startup and runtime behaviour identical to v0.18.
- [ ] Unit tests for `PluginWatcherService` in `Runtime.Plugins.Tests`:
  - Watcher triggers `ReloadAsync` on filesystem change (mock `IPluginReloader`, mock watcher events).
  - Debounce: rapid successive events within 200 ms trigger only one `ReloadAsync` call.
  - `ReloadPolicy.Disabled`: `PluginWatcherService` not registered, no watcher instantiated.
  - Reload failure: `reload-failed` log emitted; old descriptor still returned by registry.
- [ ] Full solution builds clean; 0 warnings, 0 errors; all tests green.

### PR 3 — Integration test + sample update

- [ ] Integration test `tests/Vais.Agents.Runtime.Host.Tests/PluginHotReloadIntegrationTests.cs` — 3 tests:
  - `HotReload_SwapsHandler_WithoutRestartingHost`: stages fixture plugin v1, triggers `IPluginReloader.ReloadAsync` directly (no FileSystemWatcher), asserts registry now returns v2 factory, asserts v2 `AskAsync` result.
  - `HotReload_OldAlc_IsGarbageCollected_AfterSwap`: after swap, holds `WeakReference<PluginAssemblyLoadContext>` to old ALC, GC × 3, asserts `!IsAlive`.
  - `HotReload_WithPolicy_Disabled_DoesNotRegisterWatcher`: `ReloadPolicy.Disabled`, asserts `IPluginWatcherService` not in DI.
- [ ] Two fixture plugin assemblies for integration tests: `PluginFixtureV1` (returns `"Sunny!"`), `PluginFixtureV2` (returns `"Rainy!"`). Both `ProjectReference`d with `ReferenceOutputAssembly=false`.
- [ ] Update `samples/PluginAgentWeather/README.md` — new section "Hot-reload (v0.22+)": show `ReloadPolicy = ReloadPolicy.DrainAndSwap` in `AddAgentPlugins`, demonstrate `dotnet publish` overlay + observe log line `reload-success`.
- [ ] `RuntimeOptions.ReloadPolicy` property (`PluginReloadPolicy? ReloadPolicy`; null means `Disabled`). `VAIS_PLUGINS_RELOAD_POLICY` env var (`disabled` | `drain-and-swap`). `CompositionRoot.ConfigureServices` passes it through to `PluginLoaderOptions`.
- [ ] Full solution builds clean; 0 warnings, 0 errors; all test projects green including new integration tests.

### PR 4 — Docs + PublicAPI promotion + tag

- [ ] `docs/concepts/runtime-plugins.md` — new "Hot-reload (v0.22)" section: reload model, `ReloadPolicy` enum, drain-and-swap semantics, `VAIS_PLUGINS_RELOAD_POLICY` env var, known limitations (cross-node, state migration, v0.22.x items).
- [ ] `docs/guides/package-an-agent-as-a-plugin.md` — new step 10 "Enable hot-reload" with code snippet and mount-refresh workflow.
- [ ] `docs/reference/runtime-configuration.md` — `VAIS_PLUGINS_RELOAD_POLICY` entry in the Plugin loader section.
- [ ] `docs/reference/problem-details-urns.md` — `plugin-reload-failed`, `plugin-reload-abi-mismatch` added to v0.22 plugin model section.
- [ ] `PublicAPI.Shipped.txt` promotion: `Runtime.Plugins` new surface (5 types: `IPluginReloader`, `PluginReloadResult`, `PluginReloadStatus`, `PluginReloadUrns`, `ReloadPolicy` update on `PluginLoaderOptions`).
- [ ] Milestone entry appended to `plans/actor-agents-oss-milestone-log.md`.
- [ ] `docs/roadmap/deferred-backlog.md` — strike through the hot-reload entry, add **SHIPPED v0.22**.
- [ ] Tag `v0.22.0-preview`.

Sizing: PR 1 ≈ 1-2 days, PR 2 ≈ 2-3 days, PR 3 ≈ 1-2 days, PR 4 ≈ 1 day. **Total 5-8 working days** (~1.5 weeks).

---

## Acceptance

Pillar is done when:

- [ ] `PluginAssemblyLoadContext` is collectible; old ALC is GC-eligible after swap with no references held — verified by `WeakReference.IsAlive` leak test passing (GC × 10 cycles, `false`).
- [ ] `dotnet publish` of a new plugin DLL into the watched directory triggers reload without pod restart; active agents migrated within one grace-period invocation cycle.
- [ ] Reload failure (ABI mismatch) leaves old descriptor intact; `reload-failed` log emitted with URN; runtime continues serving from old plugin.
- [ ] `ReloadPolicy.Disabled` (default): behaviour identical to v0.18 — no watcher, no extra allocations, existing 23 `Runtime.Plugins.Tests` still green.
- [ ] Cross-ALC isolation preserved under collectibility: two plugins with conflicting deps both load + work (existing test still green).
- [ ] Orleans grain deactivation triggered after swap; grains re-activate against new registry on next invoke; no `handler-not-loaded` errors post-activation.
- [ ] `VAIS_PLUGINS_RELOAD_POLICY=drain-and-swap` env var enables watcher at runtime; `disabled` (or unset) disables it.
- [ ] All three integration tests in `PluginHotReloadIntegrationTests.cs` green.
- [ ] Full solution 0 warnings / 0 errors; every test project green.
- [ ] Docs reviewed; cross-links intact from `index.md` / `architecture.md` / `runtime-configuration.md`.
- [ ] Tag `v0.22.0-preview` created.

---

## Composition-root extension — sketch

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ... v0.18 Pillar C plugin loader wiring unchanged ...
    if (!string.IsNullOrWhiteSpace(options.PluginsDirectory) && Directory.Exists(options.PluginsDirectory))
    {
        var loaderOptions = new PluginLoaderOptions
        {
            ReloadPolicy = options.ReloadPolicy ?? ReloadPolicy.Disabled   // NEW in v0.22
        };
        services.AddAgentPlugins(options.PluginsDirectory, loaderOptions); // watcher registered when DrainAndSwap
    }
    // ... rest unchanged ...
}
```

Consumer opt-in (three extra lines beyond v0.21):

```csharp
builder.Services.AddAgentPlugins("/var/lib/vais/plugins", new PluginLoaderOptions
{
    ReloadPolicy = ReloadPolicy.DrainAndSwap   // NEW in v0.22
});
```

---

## FileSystemWatcher debounce — sketch

```csharp
private CancellationTokenSource? _debounceCts;
private readonly SemaphoreSlim _debounceLock = new(1, 1);

private async void OnFileChanged(object _, FileSystemEventArgs e)
{
    await _debounceLock.WaitAsync();
    try
    {
        _debounceCts?.Cancel();
        _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        var cts = _debounceCts;
        _ = Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token)
            .ContinueWith(
                _ => _reloader.ReloadAsync(e.FullPath, _stoppingToken),
                TaskContinuationOptions.OnlyOnRanToCompletion);
    }
    finally { _debounceLock.Release(); }
}
```

---

## Risks + mitigations

- **Collectible ALC breaks shared-type carve-out.** Collectible contexts have subtle GC timing differences; a type loaded in a collectible ALC that escapes to long-lived code causes a `TypeLoadException` or holds the ALC alive. **Mitigation**: existing shared-type boundary unit test (plugin's `IAiAgent` resolves to runtime assembly) is the regression guard; run on every PR.
- **FileSystemWatcher reliability on Linux/macOS.** `FileSystemWatcher` uses `inotify` on Linux (reliable) and `kqueue` on macOS (less reliable with many dirs). Container deployments are Linux; acceptable. **Mitigation**: document macOS limitation; debounce reduces event storm risk.
- **Drain window `handler-not-loaded`.** Between registry swap and grain re-activation, a grain that was deactivated mid-invocation may surface `handler-not-loaded` to a racing caller. **Mitigation**: graceful deactivation gives the grain time to finish in-flight calls before deactivating (Orleans `RequestMessageTimeout` guard); document as known v0.22 behaviour.
- **Memory leak if caller holds old ALC reference.** If production code stores a `PluginDescriptor` reference across reload, the old ALC stays alive. **Mitigation**: `PluginDescriptor` is internal; only `IPluginHandlerRegistry` public surface is `TryGet` (returns factories, not descriptors); leak test is the correctness gate.
- **`dotnet publish` multi-file write races.** Replacing a plugin dir with `dotnet publish` writes multiple files; watcher may fire on partial state. **Mitigation**: 200 ms debounce absorbs most publish bursts; `AssemblyPluginLoader` catches `FileLoadException` and returns `LoadFailed` status (old descriptor kept).
- **`IManagementGrain` unavailable in non-Orleans hosts.** `GrainDrainService` depends on Orleans. **Mitigation**: `GrainDrainService` is optional DI — `DefaultPluginReloader` checks `IServiceProvider.GetService<IGrainFactory>() != null` before calling drain; non-Orleans hosts skip grain deactivation (acceptable: no grains to deactivate).
- **Non-collectible old `PluginAssemblyLoadContext` instances (pre-v0.22 plugins).** Partners may have built plugins compiled against v0.18's non-collectible ALC indirectly (through the descriptor type). The ALC type itself doesn't appear in plugin author's API surface, so this is runtime-internal. **Mitigation**: no action needed; collectibility is a runtime-internal property.
- **Design-exploration risk.** No confirmed partner use case. Feature shipped behind `ReloadPolicy.Disabled` default. If no partner pick-up by v0.23, evaluate deferring docs + sample to keep surface area low. **Mitigation**: flag behind env var + feature gate; safe to never enable.

---

## Progress log

- 2026-04-24 — Pillar plan created. Scope locked from codebase audit of `Vais.Agents.Runtime.Plugins`. Key decisions: drain-and-swap model, collectible ALC, FileSystemWatcher + 200 ms debounce, `IPluginReloader` interface, `PluginHandlerRegistry.SwapAsync`, `GrainDrainService` for Orleans deactivation, `ReloadPolicy.Disabled` default. Design-exploration milestone — no confirmed partner use case; ships behind feature flag. Four-PR sequence: collectible ALC + API → watcher service + swap → integration tests → docs + tag. ~5-8 working days.
- 2026-04-24 — PR 1 landed. Made `PluginAssemblyLoadContext` collectible (`isCollectible: true`). Added `ReloadPolicy` enum + `PluginLoaderOptions.ReloadPolicy` property, `IPluginReloader` interface, `PluginReloadResult` record, `PluginReloadStatus` enum, `PluginReloadUrns` class, `PluginHandlerRegistry.SwapAsync` (internal, `SemaphoreSlim`-backed), `PluginHandlerRegistry.GetAllFactories()` (internal), `AssemblyPluginLoader.LoadPlugin()` (internal), `DefaultPluginReloader` (internal impl). 4 new tests: collectible-ALC leak test + `SwapAsync` first-load, swap-replaces, `GetAllFactories` — 27 total, all green. Full solution 0 warnings / 0 errors. **Next**: PR 2 — `PluginWatcherService : IHostedService` + grain drain + DI wiring.
