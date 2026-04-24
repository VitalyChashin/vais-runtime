# Dependency-upgrade review — post `v0.2.0-preview`

Companion review to `actor-agents-oss-extraction-research.md`. Goal of this document: re-examine Phase 1's logged findings/surprises through the lens of a coordinated dependency bump to the latest stable .NET/Orleans/MEAI/SK/MAF, identify which trade-offs the bump unlocks, and propose an ordering.

Context at the time of this review: Phase 1 is complete; `v0.2.0-preview` is cut locally (not pushed). 103/103 tests green. No consumers yet.

---

## STATUS (2026-04-18): Phases A/B/C LANDED → `v0.3.0-preview` cut locally

Phases A, B, and C from this review all landed in a single session on OSS repo `main`. A local `v0.3.0-preview` was packed + smoketested but **not pushed** (same "design-partner-first" stance as `v0.2.0-preview`). Per-phase outcomes summarised below; full commit history + detailed findings in the main research doc's §8 under the `2026-04-18 — Dependency-upgrade review Phases A/B/C + v0.3.0-preview local cut` entry.

- **Phase A** (commit `af9821a`): 10.x bump landed. Hit several unplanned source breaks beyond the `thread:`→`session:` rename predicted below — MAF's `CreateAIAgent` extension was *removed* entirely (had to switch to `new ChatClientAgent(...)`); Orleans 10's new `ORLEANS0014` analyzer forbids `ConfigureAwait(false)` in grain code; Testcontainers 4.11 deprecated the parameterless `new TBuilder()` pattern; VectorData `[VectorStoreVector(Dimensions: 8)]` → positional `[VectorStoreVector(8)]`.
- **Phase B** (commit `368510c`): `UseAgenticRedisStreaming` shipped with 2 tests against real Redis via `Orleans.Streaming.Redis 10.1.0-alpha.1`.
- **Phase C** (commit `52276bf`): `ChatRole` → `AgentChatRole` clean-break rename (19 files). Both audits (polymorphic codec, MAF `ChatClientAgent` caching) came back no-op — the underlying findings from M3e-3b and M3e-2 still hold under the 10.x stack.
- **`v0.3.0-preview` cut** (commit `91f08a1`, annotated tag `v0.3.0-preview`): 11 `.nupkg` + 11 `.snupkg` in `oss/agentic/artifacts/packages/`. Consumer smoketest restored + built + ran clean, including a reflection probe confirming the new streaming extension resolves.

**Key deviations from this review's predictions** (the items wrong / under-scoped at review time):

- **FluentAssertions 6.12.3 does not exist.** The research agent's table (§1) claimed `6.12.3` Apache is the current stable on the 6.x line. NuGet has only up to `6.12.2` on 6.x. Pinned at 6.12.2. Phase E's "decide: stay on 6.12.3 vs migrate" simplifies to "stay on 6.12.2 until somebody needs a feature only in 7+/8+."
- **VectorData had to be held at 10.1, not bumped to 10.5.** SK 1.74's InMemory preview connector (`1.74.0-preview`) was compiled against VectorData 10.1's `VectorSearchFilter`, which was removed in later 10.x patches. Bumping VectorData to 10.5 broke 3 retriever tests with `TypeLoadException`. Documented the holdback in `Directory.Packages.props`; lift in lockstep with SK.Connectors.InMemory.
- **MAF `CreateAIAgent` extension was entirely removed in 1.1.0 GA**, not just renamed. The review's §2 table predicted `thread:`→`session:` rename but not the factory-pattern change. `MafCompletionProvider` now uses a `BuildAgent` helper that calls `new ChatClientAgent(chatClient, name, instructions, description)` directly.
- **Orleans `ORLEANS0014` analyzer is new in 10.x**, not called out in the review. Forbids `ConfigureAwait(false)` in grain code (must use `ConfigureAwaitOptions.ContinueOnCapturedContext` or omit). Removed six occurrences in `AiAgentGrain`; pattern is now "no ConfigureAwait inside grains" (grain code runs on the single-threaded grain scheduler, so there's no context to continue on anyway).
- **Added a local `NuGet.config`** at `oss/agentic/` — unplanned but necessary. The dev machine had a stale `D:\…\Syncfusion\…\nuget\` source that failed NU1301 on first restore after bumping packages. Clearing sources via a repo-local config isolates the OSS repo from machine-level NuGet contamination. Minor, but worth adding to the recommended "new contributor" setup list.
- **Both Phase C audits came back no-op.** Orleans 10.1 still dispatches `IConverter<TValue, TSurrogate>` by exact runtime type — dropping the three per-subclass `*SurrogateConverter` classes broke 6 tests with `ObjectCodec.ThrowCodecNotFound`. MAF 1.1.0 GA's `ChatClientAgent.Instructions` is still construction-only (no run-time override on `AgentRunOptions`), so per-call construction remains correct since `KnowledgeRetrievalFilter` can mutate `CompletionRequest.SystemPrompt`. Kept both patterns with clarifying comments; deleted the follow-up work from consideration.

Phases D (`.NET 10` multi-target), E (FluentAssertions), and F (xUnit v3) remain deferred per the recommendation below — independent low-urgency workstreams, no gating on a design-partner round.

---

---

## 1. Current pins vs. latest (as of 2026-04)

| Family | Current pin | Latest stable | Notes on the gap |
|---|---|---|---|
| `Microsoft.Extensions.AI.*` | `9.10.0` | **`10.5.0`** | MEAI hit GA on 10.x; the "9.x VectorData gap" never existed — the team rolled forward to 10. |
| `Microsoft.Extensions.VectorData.Abstractions` | `9.7.0` | **`10.5.0`** | 10.x stable and matched to MEAI 10.x. |
| `Microsoft.SemanticKernel.*` | `1.62.0` (+ `1.63.0-preview` InMemory) | **`1.74.0`** (InMemory still preview-only at 1.74) | NU1608 trap on `OpenAI` exact-pin GONE in SK 1.74. `AgentChat` deprecated in favour of MAF-backed agents. |
| `Microsoft.Agents.AI` (MAF) | `1.0.0-preview.251009.1` | **`1.1.0`** | GA shipped. `thread:` → `session:` rename landed during rc cycle. `AgentThread` → `AgentRunSession` in several public APIs. |
| `Microsoft.Orleans.*` | `9.2.1` | **`10.1.0`** | GA shipped. Orleans 10 works on `net8.0`/`net9.0` — does NOT force `net10.0`. Core is stable; Streaming.Redis is **alpha-only** (`10.1.0-alpha.1`). |
| `Microsoft.Orleans.Streaming.Redis` | — (blocked at 9.x) | **`10.1.0-alpha.1`** only | Alpha, single build, requires Orleans 10.x family (can't mix with 9.x). |
| `OpenAI` SDK | `2.5.0` | **`2.10.0`** | Forced to ≥2.9.1 by SK 1.74; to ≥2.10.0 by MEAI.OpenAI 10.5.0. |
| `M.E.Logging/DI.*` | `9.0.10` | **`10.0.6`** | Part of aligned M.E.* 10 family. |
| `M.E.Resilience` | `9.10.0` | **`10.5.0`** | |
| `OpenTelemetry` | `1.13.1` | **`1.15.2`** | Non-breaking. |
| `Npgsql` | `8.0.5` | **`9.0.5`** (for `net9.0`) | `10.0.2` requires `net10.0` TFM. |
| `StackExchange.Redis` | `2.8.16` | **`2.12.14`** | 3.0 still preview. Safe bump. |
| `Testcontainers.*` | `4.0.0` | **`4.11.0`** | Safe. |
| `xunit` | `2.9.2` | **`3.2.2`** (v3) | v2 on EOL track. Non-trivial migration (new runner `xunit.v3.runner.*`, `IAsyncLifetime` moved). |
| `FluentAssertions` | `6.12.2` | **`6.12.3`** Apache / **`8.x`** commercial | v7+ switched to **Xceed commercial licence**. Apache branch ends at 6.12.3. |
| `Microsoft.NET.Test.Sdk` | `17.11.1` | **`18.4.0`** | Required for xunit v3 and VS 17.12+ test explorer. |
| `.NET` TFM | `net9.0` | `net10.0` GA (LTS, Nov 2025) | Orleans/MEAI/SK/MAF all multi-target `net10.0`. |

---

## 2. §8 findings re-examined

Each line below is a pain point / surprise from the research doc's milestone log; the right column says what, if anything, a dependency bump changes.

| Finding (source milestone) | Still relevant after 10.x bump? |
|---|---|
| **SK 1.62's exact-version `OpenAI [2.2.0]` pin (NU1608 suppressed)** — M1 & ongoing | **RESOLVED.** SK 1.74 uses `OpenAI ≥ 2.9.1` as a floor, no brackets. The NU1608/NU1107/NU1605 suppressions in the OSS repo can be removed. |
| **MEAI 9.10 has no matching VectorData; pinned VectorData at 9.7** — M3d | **RESOLVED.** MEAI 10.5 and VectorData 10.5 are aligned. The 9.7 pin can move to 10.5; `Microsoft.SemanticKernel.Connectors.InMemory 1.74.0-preview` aligns with the new MEAI. |
| **`Microsoft.Orleans.Streaming.Redis` has no stable 9.x; had to pivot M3e-3b to provider-neutral** — M3e-3b | **UNBLOCKED (with caveat).** Orleans 10.x stable + `Streaming.Redis 10.1.0-alpha.1`. The deferred `UseAgenticRedisStreaming` convenience extension becomes a ~10-LOC addition; alpha quality is the risk. |
| **MAF `thread:` / preview API churn** — M3a/M3e-2 | **RESOLVED.** Rename is in GA. `MafCompletionProvider` + `Scripted*` doubles in tests will need source updates; no design change. |
| **`Vais2.Agents.ChatRole` collides with `Microsoft.Extensions.AI.ChatRole`** — dog-food finding post-0.1, recurred in M3e-2 tests | **INDEPENDENT OF UPGRADE, BUT TIMING WINDOW OPEN.** No consumers yet on `0.2.0-preview`. Renaming `ChatRole` → `AgentChatRole` (with a type-forwarded compatibility shim) is cheap to do during the upgrade bump; free ride on the breaking-change commit. |
| **`SkCompletionProvider` ctor eagerly resolves `IChatCompletionService`** — dog-food | **UNCHANGED.** Still valid; should tighten XML doc. Not a bump concern. |
| **MAF streaming via `AIAgent.RunStreamingAsync`** (structural parallel to non-streaming) — M3e-2 | **POSSIBLY SIMPLIFIABLE.** MAF 1.1.0's stabilised `AIAgent`/`AgentRunSession` shape may let us simplify or drop the per-turn agent reconstruction. Worth auditing on the bump. |
| **Abstract-record PublicAPI verbosity (20+ baseline entries for one polymorphic event)** — M3e-3a | **UNCHANGED.** PublicAPI analyzer behaviour is the same; `CodeAnalysis.PublicApiAnalyzers` is pinned at `3.11.0-beta1` regardless. |
| **Polymorphic surrogate dispatch by exact runtime type (4 converters for `AgentEvent`)** — M3e-3b | **WORTH RE-CHECKING.** Orleans 10 cleaned the stream-provider registration surface; may also have relaxed the polymorphic-codec story. Audit on bump; if relaxed, collapse 4 converters → 1. |
| **Memory streams need `PubSubStore` + client-side `AddMemoryStreams`** — M3e-3b | **UNCHANGED.** Documentation quirk, not a code issue. |
| **`ConfigureAwait(false)` banned in xUnit test methods by `xUnit1030`** — M3e-1 | **MIGRATION CONSIDERATION.** xUnit v3 retains the same analyzer family; no change to the rule. Migration cost is orthogonal. |
| **`NullAgentEventBus` placement (Core vs. Abstractions)** — M3e-3a | **UNCHANGED.** |
| **`@event` parameter name escaping in PublicAPI baseline** — M3e-3a | **UNCHANGED.** |
| **`TerminationPredicate` delegate needs `Invoke` in baseline** — M3e-4 | **UNCHANGED.** |
| **`AsyncLocalAgentContextAccessor.Current` is read-only (use `Push`)** — M3e-3a | **UNCHANGED.** |
| **`yield return` inside `try/catch` disallowed (nested pattern)** — M3e-2 | **UNCHANGED — language-level.** |

---

## 3. Proposed coherent upgrade path

The research agent confirmed a single consistent "everything on 10.x, `net9.0` first" path exists with no NU1608 traps. The practical ordering:

### Phase A — coordinated 10.x bump, still on `net9.0` *(1 session)*

- `Directory.Packages.props`: flip `Microsoft.Extensions.AI.*` → `10.5.0`; `Microsoft.Extensions.VectorData.Abstractions` → `10.5.0`; `Microsoft.SemanticKernel.*` → `1.74.0` (InMemory → `1.74.0-preview`); `Microsoft.Agents.AI` → `1.1.0`; `Microsoft.Orleans.*` → `10.1.0` (every package including `Streaming`, `Clustering.Redis`, `Persistence.Redis`, `Clustering.AdoNet`, `Persistence.AdoNet`); `OpenAI` → `2.10.0`; `Microsoft.Extensions.Logging/DI.*` → `10.0.6`; `Microsoft.Extensions.Resilience` → `10.5.0`; `OpenTelemetry` → `1.15.2`; `Npgsql` → `9.0.5`; `StackExchange.Redis` → `2.12.14`; `Testcontainers.*` → `4.11.0`.
- Remove the NU1608/NU1107/NU1605 suppressions from `Directory.Build.props` (SK 1.74 no longer needs them). Remove the labels in `Directory.Packages.props` that explain the OpenAI exact-pin tension.
- Fix up source for:
  - MAF `thread:` → `session:` rename in `MafCompletionProvider` + `ScriptedChatClient` / `ScriptedStreamingChatClient` / any test that reconstructs `AgentThread`.
  - Orleans 10 grain call filter / stream provider registration API changes (the research agent noted "many [Obsolete] grain call filter APIs removed" — we may not use any, but verify `AiAgentGrain` compiles clean).
  - MEAI 10.x: `IChatClient` pipeline type moves; expect a few `using` updates but no structural work.
  - SK 1.74: `AgentChat` deprecation — we don't use it (our multi-agent is neutral), but double-check.
- Run full test suite; fix mechanical breaks.

### Phase B — unlock the deferred Redis-streams slice (M3e-3b follow-up) *(1 session, small)*

- Pin `Microsoft.Orleans.Streaming.Redis` = `10.1.0-alpha.1` (document alpha risk in `Vais2.Agents.Persistence.Redis` README section).
- Add `UseAgenticRedisStreaming(ISiloBuilder, string, string? providerName = null)` in `AgenticRedisPersistenceExtensions` (~10 LOC wrapping `siloBuilder.AddRedisStreams(providerName ?? OrleansAgentEventBus.StreamNamespace, configureRedis)`).
- Add a Testcontainers Redis round-trip test to `Vais2.Agents.Persistence.Redis.Tests` that publishes through the bus and verifies delivery through real Redis.
- Update `OrleansAgentEventBus`'s XML remarks to promote Redis streams from "future" to "supported (alpha)" but keep the bus provider-neutral.

### Phase C — opportunistic simplifications *(1 session)*

- **Audit polymorphic codec story in Orleans 10.** If 10 accepts a single `IConverter<AgentEvent, …>` for polymorphic sites, drop the three per-subclass converters (`TurnStartedSurrogateConverter`, etc.) and keep only the base one.
- **MAF `AIAgent`/`AgentRunSession` cleanup.** Audit whether the per-turn `CreateAIAgent(...)` + `RunStreamingAsync` pattern in `MafCompletionProvider` can be simplified against the stabilised 1.1.0 surface.
- **`Vais2.Agents.ChatRole` → `AgentChatRole` rename.** Free-ride on the upgrade commit. Ship `[Obsolete]` type-forward on `ChatRole` for a grace release. This is the one surface change blocked by "no breaking changes after public preview" that we should grab while we still can.

### Phase D — `.NET 10` multi-target *(optional, later)*

- Change every `csproj` `TargetFramework` → `TargetFrameworks` with `net9.0;net10.0`.
- Re-run tests on both frameworks. CI matrix gets a second row.
- Publish continues to ship `net9.0` primary; `net10.0` is available for early adopters.

### Phase E — FluentAssertions licence decision *(independent, 1 session)*

- Stay on 6.12.3 (Apache-2.0 forever) for OSS purity. Vendored floor.
- Alternative: migrate to **Shouldly** or **AwesomeAssertions** (an Apache-2.0 fork of FA). Non-trivial in test code — ~100 assertion-site rewrites.
- **Recommendation**: pin `FluentAssertions = 6.12.3` with a comment; revisit if v7+ gains features we actually need.

### Phase F — xUnit v3 *(independent workstream, 1-2 sessions)*

- Migrate test projects to `xunit.v3 3.2.2` + `xunit.v3.runner.visualstudio` + `Microsoft.NET.Test.Sdk 18.4.0`.
- Source changes: `IAsyncLifetime` interface moved to `Xunit`; `[Fact]` unchanged; `Assert` mostly compatible.
- Independent of the 10.x bump — defer until after the design-partner round or bundle into a "test-housekeeping" slice.

---

## 4. Simplifications / architectural wins the bump unlocks

1. **NU1608/NU1107/NU1605 suppressions removed.** Shipped cleaner; fewer "why are these here?" questions from new contributors.
2. **`UseAgenticRedisStreaming` shippable.** Closes the M3e-3b deferred work (provider-neutral bus stays; extension adds convenience).
3. **Single consistent MEAI/VectorData pairing (both 10.x)** — removes the "VectorData pinned 3 minors behind MEAI because no 9.10 exists" comment forever.
4. **Potential collapse of polymorphic surrogate converters (4 → 1)** if Orleans 10 relaxed the exact-type dispatch.
5. **`ChatRole` rename window open** — no consumers yet on 0.2.0-preview, so a breaking rename with type-forwarded shim is cheap.
6. **MAF API churn absorbed at a settled version** — rather than chasing rc-to-rc renames during Phase 3.

---

## 5. Risks & watchpoints

- **`Microsoft.Orleans.Streaming.Redis 10.1.0-alpha.1` is a single-alpha release.** No production track record. Mitigation: keep the provider-neutral `OrleansAgentEventBus` as the primary surface; `UseAgenticRedisStreaming` is a convenience helper, easily swapped for memory-streams if alpha bites.
- **`Microsoft.SemanticKernel.Connectors.InMemory` is still preview-only at 1.74.0-preview.** No stable available, same as 1.63.0-preview. Test-only dependency — does not leak into shipped packages.
- **VAIS2 main app still uses Orleans 9.2.1.** This review concerns the OSS repo (`oss/agentic/`), which is a separate git repo. VAIS2 migration is gated on `1.0` per the roadmap; the OSS bump does not force VAIS2 to bump. But when VAIS2 eventually migrates off its internal `Backend/Actors/Vais2Agents.*` to the OSS packages, it will pick up the Orleans 10 requirement — plan for that in the VAIS2-migration slice.
- **MAF 1.1.0 still uses VectorData 9.7** while SK-InMemory preview uses 10.x — NuGet picks the higher, but the mismatch is worth logging if we see diamond-dependency warnings.
- **`Npgsql 10` requires `net10.0` TFM.** Don't bump beyond 9.0.5 until we multi-target to 10.
- **FluentAssertions silent commercial-licence trap.** A contributor blindly taking the `Update-Package` could pull 8.x and import a commercial-licence obligation. Lock the version with a comment.

---

## 6. Recommendation

**Do Phase A + Phase B + Phase C as a single `v0.3.0-alpha` (or rename-if-breaking `v0.3.0-preview`) bundle.** The ~1.5 sessions of churn is worth absorbing now, before public push, because:

- It closes the two long-standing "deferred because upstream isn't ready yet" items (VectorData 9-vs-10, Redis streams).
- It removes the NU1608 suppression scaffolding that new contributors will ask about.
- It gives us the `ChatRole` rename window before public consumers land.
- The current pins are mid-life; waiting longer means absorbing more rc/preview churn.

Defer Phases D (`.NET 10` multi-target), E (FluentAssertions), and F (xUnit v3) — they're independent, low-urgency, and each add their own risk surface.

---

## 7. Task list

### Phase A — coordinated 10.x bump (`net9.0`) ✅ commit `af9821a`

- [x] ~~Update `Directory.Packages.props` version pins across all families per §3 Phase A.~~ (done; VectorData held at 10.1 vs predicted 10.5 — see §STATUS deviations)
- [x] ~~Remove `NU1608` / `NU1107` / `NU1605` from any `NoWarn` / `<PropertyGroup>` suppressions in `Directory.Build.props` or project files.~~
- [x] ~~Fix MAF `thread:` → `session:` rename in `MafCompletionProvider` + `ScriptedChatClient` + `ScriptedStreamingChatClient`.~~ Plus the unplanned `CreateAIAgent` → `new ChatClientAgent(...)` refactor.
- [x] ~~Adjust MEAI 10 type moves (`using` cleanups).~~
- [x] ~~Verify Orleans 10 compiles `AiAgentGrain`, `OrleansAgentEventBus`, surrogates, all extensions (`AgenticRedisPersistenceExtensions`, `AgenticPostgresPersistenceExtensions`).~~ Also removed six `.ConfigureAwait(false)` from `AiAgentGrain` (new `ORLEANS0014` analyzer).
- [x] ~~Re-run full test suite; fix mechanical breaks.~~ 103/103 green (105/105 after Phase B).
- [x] ~~Re-run `artifacts/smoketest/`.~~ Deferred to the 0.3.0-preview cut (below).
- [x] ~~Commit: "chore(deps): coordinated 10.x bump — MEAI/SK/MAF/Orleans/OpenAI".~~
- [x] **Added (unplanned):** repo-local `NuGet.config` clearing global sources (dev-machine Syncfusion NuGet contamination fix).

### Phase B — `UseAgenticRedisStreaming` convenience extension ✅ commit `368510c`

- [x] ~~Add `Microsoft.Orleans.Streaming.Redis = 10.1.0-alpha.1` to `Directory.Packages.props` + `Persistence.Redis.csproj`.~~
- [x] ~~Add `UseAgenticRedisStreaming(ISiloBuilder, string, string? providerName = null)` to `AgenticRedisPersistenceExtensions`. PublicAPI.Unshipped update.~~ Final signature dropped the optional `providerName` — `RS0026` analyzer rejects paired overloads with optional parameters as a backcompat-evolution hazard. Consumers that want a custom provider name drop down to `siloBuilder.AddRedisStreams(name, ...)` directly; convention matches the existing `UseAgenticRedisClustering` / `AddAgenticRedisGrainStorage` shape.
- [x] ~~Add `IClientBuilder` overload too (symmetry with clustering extensions).~~
- [x] ~~Add Testcontainers Redis round-trip test to `Vais2.Agents.Persistence.Redis.Tests`~~ — 2 tests (TurnStarted round-trip + all-three-subclass round-trip).
- [x] ~~Update `OrleansAgentEventBus` XML remarks: promote Redis streams from "blocked" to "supported (alpha)".~~
- [x] ~~Commit: "feat(persistence.redis): UseAgenticRedisStreaming against Orleans.Streaming.Redis 10.1.0-alpha".~~

### Phase C — opportunistic simplifications ✅ commit `52276bf`

- [x] ~~Audit Orleans 10 polymorphic codec behaviour.~~ **Outcome: no simplification.** Tested by dropping the 3 per-subclass converters; 6 tests failed with `ObjectCodec.ThrowCodecNotFound`. Orleans 10.1 still resolves by exact runtime type. Reverted; added "Confirmed still required under Orleans 10.1" note.
- [x] ~~Audit `MafCompletionProvider` against MAF 1.1.0.~~ **Outcome: no simplification.** `ChatClientAgent.Instructions` is read-only (set at construction); `AgentRunOptions` has no per-run override. `KnowledgeRetrievalFilter` mutates `CompletionRequest.SystemPrompt` per call, so per-call `ChatClientAgent` construction is still the correct pattern.
- [x] ~~Rename `Vais2.Agents.ChatRole` → `Vais2.Agents.AgentChatRole`.~~ Shipped the clean break (no `[Obsolete]` type-forward) since 0.2.0-preview wasn't pushed publicly. 19 files touched via sed with a placeholder trick to protect `Microsoft.Extensions.AI.ChatRole` call sites.
- [x] ~~Update every consumer source site (src, tests, smoketest, HelloAgent).~~
- [x] ~~PublicAPI sweep.~~ Sed replaced `Vais2.Agents.ChatRole` in both Shipped baselines directly — no Unshipped churn was needed for the rename itself.
- [x] ~~Commit: "refactor: simplifications from 10.x bump + ChatRole→AgentChatRole rename".~~

### Phase D — `.NET 10` multi-target *(later, deferred)*

- [ ] Change every `csproj` `TargetFramework` → `TargetFrameworks`.
- [ ] CI matrix gets `net10.0` row.
- [ ] Validate `Npgsql 10.0.2` still works under `net9.0` TFM or gate to `net10.0`.

### Phase E — FluentAssertions decision *(independent, deferred)*

- [x] ~~Decide: stay on 6.12.3 vs migrate to Shouldly/AwesomeAssertions.~~ **Outcome: stay on 6.12.2** (6.12.3 does not exist on NuGet despite what §1 of this doc claimed; the 6.x line ends at 6.12.2). `Directory.Packages.props` comment notes the commercial-licence switch at 7.x.
- [ ] If ever migrating: evaluate Shouldly or the AwesomeAssertions fork — tracked as a future item, not blocking anything.

### Phase F — xUnit v3 migration *(independent workstream, deferred)*

- [ ] Upgrade `Microsoft.NET.Test.Sdk` → `18.4.0`.
- [ ] Switch every test project to `xunit.v3` + `xunit.v3.runner.visualstudio`.
- [ ] Fix `IAsyncLifetime` import path.
- [ ] Verify Testcontainers fixtures still behave.

### Review / cut ✅ commit `91f08a1`, annotated tag `v0.3.0-preview`

- [x] ~~Decide new version number.~~ `v0.3.0-preview` (not `-alpha`) because the `ChatRole` rename is a breaking change to the surface shipped as `v0.2.0-preview`.
- [x] ~~Run the API-freeze sweep.~~ Only Persistence.Redis had non-empty Unshipped (2 streaming extensions); the ChatRole rename had been applied directly to Shipped by the Phase C sed.
- [x] ~~`dotnet pack`, update smoketest, tag.~~ 11 .nupkg + 11 .snupkg. Smoketest refreshed to `0.3.0-preview` + added a reflection probe that resolves the new streaming extension.
- [x] ~~Append a §8 milestone log entry to the main research doc when the bump ships.~~ Done — see `2026-04-18 — Dependency-upgrade review Phases A/B/C + v0.3.0-preview local cut` entry in `actor-agents-oss-extraction-research.md` §8.
