# AGENTS.md — Vais.Agents

This file is the vendor-neutral briefing for AI coding assistants (Claude Code, Cursor, Windsurf, opencode, kiro, Aider, …) working in this repository. Human-facing onboarding lives in [`README.md`](README.md) and [`docs/index.md`](docs/index.md); that content is not duplicated here.

Conform to this file. Where it differs from a general best-practice default, prefer this file — it reflects repo-specific invariants.

---

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

## Project

`Vais.Agents` is a stack-neutral agent library for .NET plus a deployable runtime container. The library ships as 27 NuGet packages (see [`docs/reference/packages.md`](docs/reference/packages.md)); the runtime (`src/Vais.Agents.Runtime.Host/`) is packaged as a Docker image + Helm chart + docker-compose recipes.

Two AI stacks are first-class and swappable via DI: **Microsoft Semantic Kernel** (`Vais.Agents.Ai.SemanticKernel`) and **Microsoft Agent Framework** (`Vais.Agents.Ai.MicrosoftAgentFramework`). The same `StatefulAiAgent` class works against either — each adapter exercises its stack's native machinery rather than reducing both to a shared `IChatClient` pass-through.

Status: **pre-alpha / pre-release**. NuGet not yet published. Public API breaks are expected until the first tagged alpha. Trademark / NuGet-id clearance is pending for the `Vais.Agents.*` ids.

Licence: **Apache-2.0**. Copyright is held by *VAIS contributors*. Every `.cs` file carries the standard one-line header.

---

## Repository layout

| Path | Purpose |
|---|---|
| `src/` | 29 C# projects: 27 library packages + `Vais.Agents.Runtime.Host` (container entrypoint) + `Vais.Agents.Control.KubernetesOperator.Host` (operator entrypoint). |
| `tests/` | xUnit test projects. Naming convention: `<ProjectUnderTest>.Tests`. |
| `samples/` | Standalone .NET 9 console apps + YAML-only directories (27 samples). Each is a runnable demonstration of one feature; see [`samples/README.md`](samples/README.md). |
| `docs/` | All docs. Subfolders: `getting-started/`, `concepts/`, `guides/`, `tutorials/`, `reference/`, `adr/`, `roadmap/`, `contributing/`. |
| `deploy/` | `compose/` (docker-compose recipes, bases + overlays), `helm/` (runtime + operator charts), `crds/` (standalone CRDs for non-Helm installs). |
| `contracts/` | Versioned contract artefacts (JSON schemas, OPA input schema, OpenAPI) consumed by downstream repos. |
| `artifacts/` | Build outputs, including `artifacts/packages/` — the local NuGet feed samples consume from. Gitignored. |
| `plans/` | Phase / pillar planning docs, milestone log, findings. Append-only, time-stamped. |
| `.github/workflows/` | CI definitions. `ci.yml` is the authoritative build + test recipe. |
| `Directory.Build.props` | Global MSBuild defaults (target framework, analyzers, package metadata). |
| `Directory.Packages.props` | Central Package Management (CPM) — every dependency version pinned here. |
| `Vais.Agents.sln` | Solution file; covers all of `src/` and `tests/`. Samples are **not** in the solution (they opt out of CPM). |
| `NuGet.config` | Isolates this workspace from machine-wide NuGet sources. Only `nuget.org` by default. |

---

## Build & test

Use the exact commands CI uses ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). No substitutions.

```bash
dotnet restore Vais.Agents.sln
dotnet build   Vais.Agents.sln --configuration Release --no-restore
dotnet test    Vais.Agents.sln --configuration Release --no-build
```

Required: **.NET 9 SDK** (`9.0.x`). No earlier or later major. CI verifies on both `ubuntu-latest` and `windows-latest`; assume either is a supported dev platform.

Single project / single test run:

```bash
dotnet build src/Vais.Agents.Core/Vais.Agents.Core.csproj
dotnet test  tests/Vais.Agents.Core.Tests/Vais.Agents.Core.Tests.csproj
dotnet test  tests/Vais.Agents.Core.Tests/Vais.Agents.Core.Tests.csproj --filter FullyQualifiedName~StatefulAiAgentTests
```

Local NuGet preview packages (consumed by `samples/` via the `artifacts/packages/` feed configured in `NuGet.config`):

```bash
dotnet pack Vais.Agents.sln --configuration Release --output artifacts/packages
```

Samples are separate solutions. Run any sample as:

```bash
dotnet run --project samples/HelloAgent
```

Most samples are deterministic (scripted fake completion provider) and need no API key. The live-LLM sample `HelloAgent` gates on `OPENAI_API_KEY`. Orleans-persistence samples need Docker.

`TreatWarningsAsErrors=true` is on. A warning breaks the build. Do not suppress — fix.

---

## Running the runtime locally

The runtime is a published Docker image consumers pull and run. Local dev uses the provided docker-compose recipes:

```bash
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:dev .
docker compose -f deploy/compose/docker-compose.localhost.yml up
```

Overlays (`opa`, `langfuse`, `otel`, `clustered`) compose orthogonally via layered `-f` flags. See [`deploy/compose/README.md`](deploy/compose/README.md).

For Kubernetes, see [`deploy/helm/vais-agents-runtime/README.md`](deploy/helm/vais-agents-runtime/README.md) and [`docs/guides/deploy-the-runtime-to-kubernetes.md`](docs/guides/deploy-the-runtime-to-kubernetes.md).

End-to-end walkthroughs:
- [`docs/getting-started/install-the-runtime.md`](docs/getting-started/install-the-runtime.md)
- [`docs/getting-started/deploy-your-first-agent.md`](docs/getting-started/deploy-your-first-agent.md)
- [`docs/tutorials/from-zero-to-graph-in-20-minutes.md`](docs/tutorials/from-zero-to-graph-in-20-minutes.md)

---

## Code style & conventions

Enforced by [`.editorconfig`](.editorconfig) + [`Directory.Build.props`](Directory.Build.props). Summary:

- **Target framework:** `net9.0`. **Language:** `latest`. **Nullable:** `enable` (all projects). **ImplicitUsings:** `enable`.
- **File-scoped namespaces.** `csharp_style_namespace_declarations = file_scoped:warning`. Block-scoped namespaces are rejected.
- **Braces required** on single-statement blocks (`csharp_prefer_braces = true:suggestion`).
- **System usings first** (`dotnet_sort_system_directives_first = true`).
- **IDE0005** (unnecessary using) is a warning and therefore an error.
- **Indent:** 4 spaces for `.cs/.csproj/.sln/.props/.targets`; 2 spaces for `.json/.yml/.yaml/.md`.
- **Line endings:** LF. UTF-8. Final newline required. No trailing whitespace.
- **XML docs required** on every public type and member (`GenerateDocumentationFile = true` on all packable projects; missing docs produce CS1591 which is an error).
- **Determinism:** `Deterministic = true`; `ContinuousIntegrationBuild = true` when `CI=true`; `EmbedUntrackedSources = true`.
- **Suppressed at repo scope:** `SKEXP0001`, `SKEXP0010`, `SKEXP0110` (Semantic Kernel experimental surface we intentionally consume), `MEAI001` (Microsoft.Extensions.AI experimental), `CA1014` (CLSCompliant — modern .NET only).

Copyright header — put this at the top of every `.cs` file, before `using` directives:

```csharp
// Copyright (c) 2026 VAIS contributors. Licensed under the Apache License, Version 2.0.
```

---

## Public API discipline

Every public-facing project uses `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Each such project has two tracked files in its root:

- `PublicAPI.Shipped.txt` — the public surface already released. Load-bearing. Do not remove entries.
- `PublicAPI.Unshipped.txt` — additions queued for the next release.

Rules (all enforced as warnings → errors):

- **RS0016** — added public API must appear in `PublicAPI.Unshipped.txt`. The analyzer tells you the exact line to add.
- **RS0017** — deleted public API must be removed from `PublicAPI.Shipped.txt` (and is a breaking change).
- **RS0025** — no duplicate declarations across files.
- **RS0026** — no new overloads of default-param methods.
- **RS0037** — nullability must be tracked on public API.

**At a milestone (annotated preview tag).** Concatenate each project's `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt`, then empty `Unshipped`. This is part of release, not development.

Never edit `PublicAPI.Shipped.txt` outside of a release — it's your commitment to consumers.

---

## Dependency management — Central Package Management (CPM)

All versions are pinned in [`Directory.Packages.props`](Directory.Packages.props). Library `.csproj` files reference packages without a `Version=` attribute.

Rules:

1. **Don't add `<PackageVersion>` loosely.** Read the existing `<ItemGroup Label="...">` blocks — they're grouped by purpose (MEAI, SK adapter, MAF adapter, MCP, A2A, OpenAI, Orleans, Redis, Postgres, tests, etc.). Add to the right group.
2. **Don't bump a version in isolation.** Several version chains are load-bearing: SK 1.74 ↔ `Microsoft.Extensions.VectorData.Abstractions` 10.1.0, MEAI 10.5 ↔ SK 1.74 ↔ `OpenAI` ≥ 2.10.0, Orleans 10.1 family, MCP 1.2 family, A2A 1.0.0-preview2 family. Comments on the ItemGroups encode the constraint — read them.
3. **Samples opt out of CPM.** Each sample `.csproj` sets `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` and pins its own versions explicitly. This keeps samples reproducible as standalone demonstrators.
4. **No pre-release drift.** If a pin is on a pre-release (e.g. `Microsoft.Orleans.Streaming.Redis` on `10.1.0-alpha.1`), the comment explains why. Do not "update" to a newer pre-release without reading the comment and the related ADR.

---

## Testing

- **Framework:** xUnit (2.9.x). Assertions via **FluentAssertions 6.12.x** (6.x is Apache-2.0; 7+ switched to commercial Xceed licence — pin stays at 6.x). Mocks via **NSubstitute 5.3.x**.
- **One test project per `src/` project.** Same namespace, `.Tests` suffix.
- **Integration tests** that need infrastructure use **Testcontainers** (Redis, Postgres). Tests gated on `OPENAI_API_KEY` are opt-in and skipped in CI.
- **Orleans** grain tests use `Microsoft.Orleans.TestingHost`.
- **ASP.NET Core** integration tests use `Microsoft.AspNetCore.TestHost`.
- "Green" means: build succeeds on both OSes, all tests pass, zero warnings.

---

## Samples policy

27 samples under `samples/`. Each:

- Is a standalone .NET 9 console app **or** a YAML-only configuration directory (for runtime / deployment scenarios).
- Has a `README.md` describing scenario, prerequisites, run command, expected output.
- Consumes `Vais.Agents.*` through `PackageReference` against the local `artifacts/packages/` feed configured in `NuGet.config` — **not** through `ProjectReference`.
- Opts out of CPM and pins package versions explicitly in its own `.csproj`.
- Prefers hermetic / deterministic providers (scripted fake `ICompletionProvider`) over live-LLM calls unless the sample is about the LLM call itself.
- Is listed in [`samples/README.md`](samples/README.md) with: name, pillar, packages used, LoC, API-key requirement, related doc.

Before adding a sample, check the table in `samples/README.md` — if the scenario is already covered, extend the existing sample rather than creating a near-duplicate.

---

## Release & versioning

- Scheme: `0.X.0-preview` (`X` = pillar number). Example: `v0.20.0-preview`.
- Release is an **annotated git tag** on the relevant main-branch commit. Tag message summarises pillar / pillars shipped.
- Packages inherit version from `Directory.Build.props` → `VersionPrefix` + `VersionSuffix` (overridden per-tag at pack time in CI when CI is wired). Do not commit a version bump without tagging.
- **Changelog discipline.** [`CHANGELOG.md`](CHANGELOG.md) is the human-facing record of every notable change. Rules:
  - Every feature, fix, or breaking change goes under `## [Unreleased]` as it lands — don't batch them at release time.
  - Use the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) section categories: `Added`, `Changed`, `Fixed`, `Removed`, `Deprecated`, `Security`.
  - Breaking API changes must be listed under `Changed` with migration guidance (old signature → new signature, what callers must do).
  - At a milestone, rename `[Unreleased]` to the new version + date (e.g. `## [0.24.0-preview] — 2026-05-01`), add a fresh empty `## [Unreleased]` block above it.
- At a milestone:
  1. Tick every completed item in the current phase / pillar plan under `plans/`.
  2. Append a dated entry to [`plans/actor-agents-oss-milestone-log.md`](plans/actor-agents-oss-milestone-log.md) summarising what shipped.
  3. Rename `[Unreleased]` in `CHANGELOG.md` to the new version block; add a new empty `[Unreleased]` section above it.
  4. Promote `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` for every affected project.
  5. Create the annotated tag.
  6. Add a "Deferred to the next pillar" bullet to the milestone log for each item that slipped.

A "pillar" is a coherent feature set scoped to one preview version. A phase is a group of pillars pursuing one thematic goal (see `plans/`).

---

## Documentation

[`docs/index.md`](docs/index.md) is the canonical entry point. Hierarchy:

- `getting-started/` — the first-hour path. One page per onboarding task. Plain-English, recipe-shaped.
- `concepts/` — one page per pillar. Explains *what* and *why*, lists core types, extension points, known limitations.
- `guides/` — task-focused recipes. "How do I X?" Each guide is backed by a sample.
- `tutorials/` — longer multi-step walkthroughs that cross pillars (e.g. zero-to-graph).
- `reference/` — lookup tables. Package list, event closed-hierarchies, URN taxonomy, CRD schema, CLI subcommands, runtime config knobs, telemetry keys.
- `adr/` — architecture decision records. One ADR per non-obvious design call. Numbered + dated + status-tagged.
- `roadmap/` — forward-looking: deferred backlog, phase proposals.
- `contributing/` — onboarding for humans + AI assistants who want to contribute.

Rules:

- **Relative Markdown links only.** No external shortlinks.
- **Code snippets must compile** against the version being documented. If it won't compile, mark it `// pseudocode` and explain.
- **Link from `docs/index.md`** — an unlinked doc is an unfindable doc.
- **Concept page ↔ guide ↔ sample.** Cross-link so a reader can jump between design rationale, recipe, and runnable code.
- **When to write an ADR vs. a concept page.** ADR = a single decision + its alternatives + its consequences, frozen in time. Concept page = the current state of a pillar, kept up to date. When a concept page starts describing "we tried X, it didn't work, we picked Y" — that belongs in an ADR the concept page links to.
- **DO NOT COMMIT `plans/` to OSS repo.** Plans is internal development folder, which should be invisible on OSS level, and used only for local development.
---

## Commits & pull requests

- **Style:** Conventional-commits-ish. `feat(area):`, `fix(area):`, `docs(area):`, `chore(area):`, `test(area):`, `refactor(area):`. Areas map to top-level folders or `src/` project names without the `Vais.Agents.` prefix (e.g. `feat(core):`, `docs(graph):`, `fix(runtime-host):`).
- **Body:** honest about intent. What changed and why. Not a paragraph marketing the feature.
- **Trailers:** every commit authored by an AI assistant carries a `Co-Authored-By:` trailer naming the assistant and model. For Claude Code the convention is `Co-Authored-By: Claude <model-id> <noreply@anthropic.com>`; adapt for other tools.
- **Never `--amend`** a pushed commit or one the user hasn't explicitly asked you to amend. **Never `--no-verify`** — fix the hook failure instead.
- **Never force-push** `main`. Never destructive git ops without explicit user instruction.
- **PR description:** what + why + test evidence. Link the plan / ADR / issue that motivated the change.

---

## Agent etiquette

Guidance for AI assistants working in this repo:

1. **Read before edit.** Open the file first; edit against actual content, not expected content.
2. **Prefer `Edit` over `Write`** for existing files — preserves trailing newlines, file mode, and minimises diff churn.
3. **Do not start dev servers proactively.** `dotnet run`, `docker compose up`, `helm install` — these are user-initiated. Print the command instead.
4. **Respect the dual-repo boundary (if applicable).** If this workspace lives inside a parent repo as a git submodule or subtree, commit to the OSS repo (`oss/agentic/`) separately from the parent.
5. **Trust the CI recipe.** If the CI workflow uses `dotnet build --configuration Release --no-restore`, don't invent `--force` or `--no-cache`.
6. **Check existing plans before starting new work.** `plans/actor-agents-oss-milestone-log.md` captures decisions made. If your task touches a pillar, open that pillar's triplet of plan + spike + findings docs.
7. **Use the Public API analyzer as your guide.** When it complains, it's right. Don't edit `PublicAPI.Shipped.txt` to silence it.
8. **Every public type needs XML docs.** If you add one without a doc comment, CI will fail.
9. **When in doubt about scope, ask.** Large unsolicited refactors are rejected on principle — see [`CONTRIBUTING.md`](CONTRIBUTING.md).
10. **Record deferred work.** If your PR defers something, add it to [`docs/roadmap/deferred-backlog.md`](docs/roadmap/deferred-backlog.md) with a dated entry.
11. **Update `CHANGELOG.md` for every notable change.** Add an entry under `## [Unreleased]` — `Added` for new features, `Changed` for behaviour or API changes (include migration guidance for breaking changes), `Fixed` for bug fixes. Do this in the same commit as the change, not as a follow-up.

---

## Where to learn more

- [`README.md`](README.md) — one-page elevator pitch + 30-second hello.
- [`docs/index.md`](docs/index.md) — full doc tree.
- [`docs/concepts/architecture.md`](docs/concepts/architecture.md) — the 27-package layering + dependency rules.
- [`docs/reference/packages.md`](docs/reference/packages.md) — per-package install guidance.
- [`samples/README.md`](samples/README.md) — learning path through the 27 samples.
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — contribution workflow and ground rules.
- [`plans/actor-agents-oss-milestone-log.md`](plans/actor-agents-oss-milestone-log.md) — dated history of every release + what's deferred next.
- [`docs/contributing/ai-assistants.md`](docs/contributing/ai-assistants.md) — recommended MCP integrations + skill categories for AI assistants working in this repo.
