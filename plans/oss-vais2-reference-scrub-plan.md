# Plan: scrub VAIS2 references from `oss/agentic/` + rename package prefix to VAIS brand

**Created:** 2026-04-19. **Updated:** 2026-04-19 (brand decision landed — **VAIS**).
**Scope:** inside `oss/agentic/` only (the OSS repo). The monorepo's `plans/`, `specs/`, `Backend/`, `Frontend/`, `k8s/` remain internal — they aren't pushed.
**Brand decision:** the OSS library publishes under the **VAIS** brand. Package namespace / assembly-name / package-id prefix `Vais2.Agents.*` → **`Vais.Agents.*`**. This was deferred in the earlier draft of this plan; it's now in scope and drives most of the mechanical work.

---

## 1. Research summary — findings by bucket

Exhaustive case-insensitive grep for `vais2` across `oss/agentic/`. ~25 "content" locations across 12 files + the cross-cutting namespace-prefix rename.

### 1.1 Legal / attribution (non-negotiable; ~3 spots)

- `LICENSE` line 189 — `Copyright 2026 VAIS2 Platform contributors`
- `NOTICE` lines 2, 4 — copyright + attribution stub
- `Directory.Build.props` lines 25–27 — `<Authors>`, `<Company>`, `<Copyright>` all reference "VAIS2 Platform contributors"

### 1.2 README narrative (external-facing; 2 spots)

- `README.md` line 8 — "Born from the [VAIS2 Platform](../..) internal `Vais2Agents.*` projects"
- `README.md` line 56 — points to `../../plans/actor-agents-oss-extraction-research.md` in the parent repo

### 1.3 ADR 0002 (editorial; 4 spots)

- `docs/adr/0002-otel-genai-conventions.md` lines 25, 32, 40, 56 — all frame the design relative to the VAIS2 parent.

### 1.4 XML-doc / inline comments (editorial; 3 spots)

- `AgenticDiagnostics.cs` line 72 — `// Vais2-specific extensions`
- `OrleansAgentContextAccessor.cs` line 15 — XML doc `/// The keys this accessor reads match the <c>vais2.*</c> tag names`
- `Directory.Packages.props` lines 29, 33 — `Label=` comments leak internal process

### 1.5 Runtime identifiers — HIGH friction (5 public constants + stream/storage names)

Ship in telemetry / storage conventions:

| Symbol | File | Current value |
|---|---|---|
| `OrleansAgentEventBus.StreamNamespace` | `Hosting.Orleans/OrleansAgentEventBus.cs:45` | `"vais2.agents.events"` |
| `AiAgentGrain.StorageName` | `Hosting.Orleans/AiAgentGrain.cs:37` | `"vais2.agents"` |
| `AgenticTags.AgentName` | `Core/AgenticDiagnostics.cs:75` | `"vais2.agent.name"` |
| `AgenticTags.UserId` | `Core/AgenticDiagnostics.cs:78` | `"vais2.user.id"` |
| `AgenticTags.TenantId` | `Core/AgenticDiagnostics.cs:81` | `"vais2.tenant.id"` |
| `AgenticTags.CorrelationId` | `Core/AgenticDiagnostics.cs:84` | `"vais2.correlation.id"` |

### 1.6 Runtime identifiers — LOW friction (3 defaults / internals)

- `MafCompletionProvider.cs:66` — default `agentName = "vais2-agent"` parameter
- `LangfuseEnrichmentOptions.cs:16` — `DefaultTags` seeds `"vais2-agents"`
- `MafCompletionProvider.cs:188` — internal anonymous-call-id prefix `"__vais2_anon_"`

### 1.7 Package-prefix rename scope (cross-cutting; the largest mechanical block)

**13 package ids + assembly names** currently shaped as `Vais2.Agents.*`:

```
Vais2.Agents.Abstractions
Vais2.Agents.Core
Vais2.Agents.Ai.SemanticKernel
Vais2.Agents.Ai.MicrosoftAgentFramework
Vais2.Agents.Hosting.InMemory
Vais2.Agents.Hosting.Orleans
Vais2.Agents.Persistence.Redis
Vais2.Agents.Persistence.Postgres
Vais2.Agents.Persistence.VectorData
Vais2.Agents.Observability.OpenTelemetry
Vais2.Agents.Observability.Langfuse
Vais2.Agents.Protocols.Mcp
Vais2.Agents.Protocols.A2A
```

All rename to `Vais.Agents.*` (drop the "2"). This touches:

- Every `.csproj` (`<RootNamespace>`, `<AssemblyName>`, `<PackageId>` if explicit, `<Description>`, `<PackageTags>`, `<ProjectReference Include="…\Vais2.Agents.*\…">`).
- Every `namespace Vais2.Agents…` declaration across ~80 `.cs` files.
- Every `using Vais2.Agents…` directive.
- Every `InternalsVisibleTo("Vais2.Agents…")` attribute.
- Every `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` entry across all 13 packages (≈594 shipped entries in Abstractions alone).
- `Vais2.Agents.sln` (rename file + update project paths).
- `samples/HelloAgent/` (`using` + project refs).
- `tests/Vais2.Agents.*.Tests/` (project dir names, `using`, `InternalsVisibleTo` targets, namespace declarations).
- `artifacts/smoketest/` (package refs via `<PackageReference Include="Vais2.Agents.*" …>`, `using`, `Program.cs`).
- `artifacts/packages/` (will regenerate on `dotnet pack` under new ids).
- `README.md` code snippets.
- CI `.github/workflows/ci.yml` if it names the solution file.

### 1.8 Sensitive leakage (audit clean)

No user emails, hardcoded machine paths, internal domains, or API keys under `oss/agentic/`. CI clean. Samples clean. NuGet.config clean (whitelist-only).

---

## 2. Decisions

### D1 — Brand-prefix: `Vais.Agents.*`

Package namespace, assembly names, package ids: **`Vais2.Agents.*` → `Vais.Agents.*`**. The "2" was an artefact of the VAIS2 Platform parent's version; the new VAIS brand drops it. No type-forwarders, no `[Obsolete]` shims — nothing is published yet, so the rename is a clean break. (If a design-partner already has a local `0.4.0-preview` copy installed, they take a version bump and a find-replace on their end; acceptable cost since there are no design partners at this stage.)

### D2 — Runtime-identifier prefix: `vais.*` (OTel tags) / `vais.agents.*` (Orleans stream + storage)

Two distinct namespaces:

**OTel tag keys** — `vais.*` (short brand namespace, matches the `langfuse.*` precedent for Langfuse's own extension tags):

| Constant | Old value | New value |
|---|---|---|
| `AgenticTags.AgentName` | `vais2.agent.name` | `vais.agent.name` |
| `AgenticTags.UserId` | `vais2.user.id` | `vais.user.id` |
| `AgenticTags.TenantId` | `vais2.tenant.id` | `vais.tenant.id` |
| `AgenticTags.CorrelationId` | `vais2.correlation.id` | `vais.correlation.id` |

**Orleans storage / streams** — `vais.agents.*` (brand + sub-product; leaves room for future non-agent storage / streams under the same brand):

| Constant | Old value | New value |
|---|---|---|
| `OrleansAgentEventBus.StreamNamespace` | `vais2.agents.events` | `vais.agents.events` |
| `AiAgentGrain.StorageName` | `vais2.agents` | `vais.agents` |

### D3 — Internal class / type prefixes: keep `Agentic*`

Alternatives: (a) keep the `Agentic*` prefix on internal helper / extension-method classes (`AgenticTags`, `AgenticDiagnostics`, `AgenticMetrics`, `AgenticOpenTelemetryExtensions`, `AgenticLangfuseExtensions`, `AgenticHostingOrleansServiceCollectionExtensions`, `AgenticHostingInMemoryServiceCollectionExtensions`, `AgenticRedisPersistenceExtensions`, `AgenticPostgresPersistenceExtensions`); (b) rename to `Vais*` or `VaisAgents*`.

**Decision: keep `Agentic*`.** Rationale: `Agentic*` is a *descriptor* ("agent-library-related helpers"), not a *brand*. Renaming to `VaisTags` / `VaisDiagnostics` / `VaisHostingOrleansServiceCollectionExtensions` confuses brand with concept — the brand already lives in the fully-qualified type name (`Vais.Agents.Core.AgenticTags`). Brands change; descriptors don't. Keeping `Agentic*` also avoids a second public-API churn on the 20+ symbols that use it. Revisit only if user feedback suggests the prefix confuses readers.

### D4 — Copyright attribution

`Copyright (c) 2026 VAIS2 Platform contributors` → **`Copyright (c) 2026 VAIS contributors`**. Applies to `LICENSE`, `NOTICE`, `Directory.Build.props`, every file header.

Scope to consider: every `.cs` file starts with `// Copyright (c) 2026 VAIS2 Platform contributors.` in the license header. Mechanical find-replace across ~80 .cs files in src/ + tests/.

### D5 — Directory.Packages.props label comments

Rewrite MCP + A2A `Label=` comments to plain-text package descriptions (no internal process context, no `<` / `>` per the known MSBuild trap). Example: "MCP adapter (Vais.Agents.Protocols.Mcp). Tracks stable ModelContextProtocol.Core 1.2+."

### D6 — PackageProjectUrl placeholder

`Directory.Build.props:29` currently `https://example.invalid/vais2-agents`. Change to `https://example.invalid/vais-agents`. Replace with the real GitHub URL on push.

### D7 — Solution file rename

`Vais2.Agents.sln` → `Vais.Agents.sln`. Update any references (CI workflow if present).

### D8 — Monorepo working directory

Currently `oss/agentic/`. **Keep as is.** It's an internal-convenience directory name, not something the OSS publish step exposes. Renaming to `oss/vais-agents/` is cheap but pointless at this stage — the published repo gets a GitHub-level name when we push, independent of the on-disk path.

### D9 — PublicAPI sweep strategy

Rename every `Vais2.Agents.` occurrence in `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` files across all 13 packages via find-replace. Don't use `*REMOVED*` markers — this is a pre-public rename, not an API change. Old entries move to new entries in a single commit; the analyzer sees "shipped API X removed, shipped API Y added" but since no consumers exist, breaking-change accounting is moot. Verify strict build passes with zero `*REMOVED*` lines afterwards.

### D10 — One mechanical sweep, then a verification sweep

Two-commit or single-commit? Single commit: easier to review holistically, single-point-of-trust rename. Two-commit (content scrub + namespace rename): isolates failure surfaces. **Decision: single commit.** The content-scrub items are small; the namespace rename is mechanical; running them together lets the test suite + PublicAPI analyzer + smoketest catch any drift in one shot.

---

## 3. Task list

### Phase A — Prep

- [ ] **T1: Snapshot.** Stash any uncommitted work in `oss/agentic/`. Tag the pre-rename commit in the OSS repo as `pre-vais-rename` (lightweight tag) so rollback is one command.
- [ ] **T2: `sed` plan dry-run.** Script a find-replace of `Vais2.Agents` → `Vais.Agents` across `oss/agentic/` excluding `artifacts/packages/` and `bin/` / `obj/`. Preview the diff; expected to hit ~80 .cs files + ~30 csproj/config/doc files.

### Phase B — Package prefix rename (mechanical)

- [ ] **T3: Rename src csproj + projdir basenames.** Rename 13 project folders under `src/` from `Vais2.Agents.*` → `Vais.Agents.*`; rename each `.csproj` correspondingly; update `<RootNamespace>` / `<AssemblyName>` / `<Description>` / `<PackageTags>` inside each csproj.
- [ ] **T4: Rename test csproj + projdir basenames.** Same for `tests/Vais2.Agents.*.Tests/` → `tests/Vais.Agents.*.Tests/`.
- [ ] **T5: Solution file.** `Vais2.Agents.sln` → `Vais.Agents.sln`. Regenerate project-path entries via `dotnet sln … add/remove` or manual edit.
- [ ] **T6: `namespace Vais2.Agents…` → `namespace Vais.Agents…`.** Sweep all `.cs` files under `src/` + `tests/` + `samples/` + `artifacts/smoketest/`. Verify no `Vais2` left except in plan/doc files (which stay with history).
- [ ] **T7: `using Vais2.Agents…` directives.** Same sweep.
- [ ] **T8: `InternalsVisibleTo("Vais2.Agents…")` attributes.** Same sweep (in .cs files) + any `<InternalsVisibleTo Include="Vais2.Agents…">` MSBuild items in csprojs.
- [ ] **T9: `<ProjectReference Include="…/Vais2.Agents.*/…">` paths.** Update every cross-project reference now that folders have moved.
- [ ] **T10: `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` rename sweep.** Find-replace `Vais2.Agents.` → `Vais.Agents.` across all 13 packages' API files. Verify format stays intact (leading `#nullable enable`, sort order, no stray whitespace).
- [ ] **T11: `samples/HelloAgent/`** — project ref paths, `using` directives, any hardcoded package-id references in comments.
- [ ] **T12: `artifacts/smoketest/`** — `<PackageReference Include="Vais2.Agents.*" Version="0.4.0-preview">` → `Vais.Agents.*`. Update `Program.cs` `using` directives.

### Phase C — Content scrub (editorial)

- [ ] **T13: Legal metadata.** `LICENSE`, `NOTICE`, `Directory.Build.props` (`<Authors>`, `<Company>`, `<Copyright>`). "VAIS2 Platform contributors" → "VAIS contributors".
- [ ] **T14: File-header comments.** Sweep all `.cs` files under `src/` + `tests/` + `samples/` + `artifacts/smoketest/`. Replace `// Copyright (c) 2026 VAIS2 Platform contributors.` → `// Copyright (c) 2026 VAIS contributors.`.
- [ ] **T15: README rewrite.** Drop line 8's parent-project paragraph and line 56's link to the parent plan doc. Rewrite the intro as an independent VAIS Agents project description. Use new package names (`Vais.Agents.*`) in any code snippets.
- [ ] **T16: ADR 0002 editorial.** Rewrite the 4 VAIS2-mentioning lines to stand alone. Update prefix references from `vais2.*` to `vais.*` throughout the ADR text.
- [ ] **T17: XML doc / inline comment scrub.** `AgenticDiagnostics.cs:72`, `OrleansAgentContextAccessor.cs:15`, and any other stray `vais2` reference in comments / XML docs.
- [ ] **T18: Directory.Packages.props label comments.** Per D5 — plain-text package descriptions, no angle brackets in `Label=` attributes.
- [ ] **T19: PackageProjectUrl placeholder.** Per D6 — `/vais2-agents` → `/vais-agents`, still `example.invalid` until real push.

### Phase D — Runtime identifier rename (public-API change)

- [ ] **T20: Rename `AgenticTags` constants.** Per D2. Update values: `vais.agent.name`, `vais.user.id`, `vais.tenant.id`, `vais.correlation.id`. Update `PublicAPI.Unshipped.txt` for Core (const-value changes may or may not fire analyzer warnings; if they do, `*REMOVED*` marker + new entry).
- [ ] **T21: Rename Orleans constants.** `OrleansAgentEventBus.StreamNamespace` → `vais.agents.events`. `AiAgentGrain.StorageName` → `vais.agents`. Update `Hosting.Orleans/PublicAPI.Unshipped.txt` for any const-value diffs.
- [ ] **T22: Langfuse enricher re-verify.** Reads `AgenticTags.*` by reference; tracks the rename automatically. Confirm via observability tests.
- [ ] **T23: Low-friction defaults.** `MafCompletionProvider.cs:66` default `agentName` → `"vais-agent"`. `LangfuseEnrichmentOptions.cs:16` `DefaultTags` → `"vais-agents"`. `MafCompletionProvider.cs:188` anon prefix → `"__vais_anon_"`.

### Phase E — Verification

- [ ] **T24: Full build.** `dotnet build Vais.Agents.sln -c Release` — clean, 0 errors, 0 warnings, PublicAPI analyzer passes.
- [ ] **T25: Full non-container test suite.** 280/280 green expected. Any red is a rename miss.
- [ ] **T26: Repack all 13 packages** at `0.4.0-preview` under new package ids. Purge old `Vais2.Agents.*.0.4.0-preview.nupkg` from `artifacts/packages/` + cached copies from `E:/nugets/vais2.agents.*/` (see MCP-bump / streaming-bump milestone findings for the cache-purge pattern).
- [ ] **T27: Smoketest rerun.** Update the smoketest's package references + `using` directives to `Vais.Agents.*`, run against refreshed feed. Confirm every pillar probe line prints cleanly.
- [ ] **T28: Final grep audit.** `rg -i vais2 oss/agentic/` from the OSS repo root returns **zero matches** (every trace gone; `Vais.Agents.*` remains). Record output in the milestone-log entry.
- [ ] **T29: Milestone-log entry.** Write a dated `2026-04-XX — Brand rename: VAIS2 → VAIS` entry covering what changed, the `*REMOVED*` decision for PublicAPI, test / smoketest status, and the tag-move implication (the `v0.4.0-preview` tag at commit `9c73a4b` points at pre-rename state — it won't be moved; the rename lands on top alongside the three post-freeze follow-ups, all bundled into whatever the next tag becomes, e.g. `v0.4.1-preview` or `v0.5.0-preview`).

---

## 4. Cross-plan impact

The rename affects the three companion plans — minor updates:

- **`oss-documentation-plan.md`**: every `Vais2.Agents.*` in code snippets → `Vais.Agents.*`. Telemetry-keys reference page uses the new `vais.*` OTel values. No structural change; just post-rename editorial.
- **`oss-samples-plan.md`**: every sample's `<PackageReference>` uses `Vais.Agents.*`. No structural change. (Land *after* the scrub so samples are born under the new name.)
- **`oss-intermediate-report-plan.md`**: deck content uses `Vais.Agents.*` names and `vais.*` OTel keys. Design-system doc references the VAIS brand (not VAIS2). Verification task R25 already cross-checks this.

---

## 5. Deferred / explicitly out of scope

- **Internal `Agentic*` class-prefix rename** (per D3). Revisit if user feedback shows reader confusion.
- **Repository rename on GitHub** — pre-push task, not this scrub. Whatever repo name lands will be the published name.
- **Migration guide for consumers upgrading across the rename** — not needed; nothing is published yet.
- **On-disk monorepo directory rename** (`oss/agentic/` → `oss/vais-agents/`) — per D8, not worth the friction at this stage.

---

## 6. Exit criteria

1. Zero `vais2` / `VAIS2` matches in `oss/agentic/` (case-insensitive grep).
2. Every package, namespace, using, attribute, PublicAPI entry, csproj, and doc snippet uses `Vais.Agents.*`.
3. Solution file is `Vais.Agents.sln`.
4. Copyright attribution reads "VAIS contributors" throughout.
5. OTel tags: `vais.agent.name` / `.user.id` / `.tenant.id` / `.correlation.id`. Orleans stream namespace `vais.agents.events`; grain storage `vais.agents`.
6. `dotnet build` clean; 280/280 non-container tests green; PublicAPI analyzer passes.
7. 13 packages repacked at `0.4.0-preview` under new ids in `artifacts/packages/`.
8. Smoketest (updated to `Vais.Agents.*` refs) runs clean against the refreshed feed.
9. Milestone log entry written.
