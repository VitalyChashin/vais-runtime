# Contributing to Vais.Agents

Thanks for considering a contribution. While the project is in Phase 1 (pre-alpha), scope is narrow and cadence is driven by the maintainers — large unsolicited PRs may be rejected on scope even if technically fine. Please open an issue first for anything beyond a bug fix or doc tweak.

## Ground rules

- Apache 2.0 contributions only. By submitting a PR you agree your work is licensed under the project's [LICENSE](LICENSE).
- No AI-stack lock-in in `Vais.Agents.Abstractions` or `Vais.Agents.Core`. Those projects must not reference `Microsoft.SemanticKernel.*`, `Microsoft.Agents.AI.*`, or Orleans. Adapters go in their own package.
- Public API changes require updating `PublicAPI.Unshipped.txt`. The analyzer will tell you what to add — see [Public API workflow](#public-api-workflow) below.
- Every public type needs an XML doc comment. `TreatWarningsAsErrors` is on, so missing docs fail the build.

## Contribution scope

What is welcome:

- **Bug fixes** in library code (`src/`). Must come with a test that would have caught the bug.
- **Docs and sample fixes** — typos, broken links, code snippets that no longer compile, clarifications.
- **New samples** demonstrating an existing feature in a new shape. Check [`samples/README.md`](samples/README.md) first; extend an existing sample if the scenario is close.
- **Issue reports** with a minimal repro — even if you don't have a fix.

What needs an issue first:

- **New features** or API additions. The maintainers may have a different shape in mind.
- **Behavioural changes** to existing public API, even if the signature is unchanged.
- **Refactors** that touch more than a few files.

What is likely to be rejected:

- **Breaking changes** without prior maintainer sign-off on the migration story.
- **Unsolicited "cleanup" PRs** that rename, restructure, or reformat existing code. The repo has a strong style preference encoded in `.editorconfig` + `Directory.Build.props`; deviations are deliberate, not accidental.
- **PRs that introduce a new top-level dependency** without justification. Central Package Management ([`Directory.Packages.props`](Directory.Packages.props)) pins every version for a reason.
- **PRs that bypass `Vais.Agents.Abstractions` to take a direct dependency on Semantic Kernel / Microsoft Agent Framework / Orleans from `Vais.Agents.Core`.** Stack-neutrality of the core is a hard invariant.

When in doubt, open an issue describing the problem you're trying to solve before writing code.

## Build and test locally

Requires **.NET 9 SDK** (`9.0.x`). No earlier or later major. CI verifies on `ubuntu-latest` and `windows-latest`; either is a supported dev platform.

```bash
dotnet restore Vais.Agents.sln
dotnet build   Vais.Agents.sln --configuration Release --no-restore
dotnet test    Vais.Agents.sln --configuration Release --no-build
```

These are the exact commands CI runs ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). The build is expected to be zero-warning; `TreatWarningsAsErrors = true` will fail otherwise.

**Single project / single test:**

```bash
dotnet build src/Vais.Agents.Core/Vais.Agents.Core.csproj
dotnet test  tests/Vais.Agents.Core.Tests/Vais.Agents.Core.Tests.csproj
dotnet test  tests/Vais.Agents.Core.Tests/Vais.Agents.Core.Tests.csproj --filter FullyQualifiedName~StatefulAiAgentTests
```

**Optional test prerequisites:**

| Prerequisite | What it unlocks |
|---|---|
| `OPENAI_API_KEY` env var | The handful of opt-in integration tests that hit a real LLM. Skipped by default; skipped in CI. |
| Docker | Testcontainers-backed tests (`Vais.Agents.Persistence.Redis.Tests`, `Vais.Agents.Persistence.Postgres.Tests`, OPA integration tests). Skipped if Docker isn't running. |

**Bug fixes in library code must be covered by a test.** If you fix a bug in `src/`, write or extend a test that would have caught it. Sample code (`samples/`) is exempt — samples demonstrate features, not correctness. If a test is genuinely impossible (e.g., requires a real external service with no seam for injection), document why in the PR description.

For analyzer details, central-package-management rules, and per-project conventions, see [`AGENTS.md`](AGENTS.md).

## Public API workflow

Every public-facing project uses `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Each such project has two tracked files in its root:

- `PublicAPI.Shipped.txt` — the public surface already released. Load-bearing. **Do not remove entries** outside a release.
- `PublicAPI.Unshipped.txt` — additions queued for the next release.

Rules (all enforced as warnings → errors):

| Code | Meaning | What to do |
|---|---|---|
| **RS0016** | Added public API not declared | Add the line the analyzer prints to `PublicAPI.Unshipped.txt`. |
| **RS0017** | Deleted public API still in `Shipped.txt` | Removing public API is a breaking change. Discuss in an issue first. |
| **RS0025** | Duplicate declaration across files | Pick one. |
| **RS0026** | New overload of a default-param method | Either add overloads instead of defaults, or add the missing overloads explicitly. |
| **RS0037** | Nullability not tracked on public API | Use `?` and `!` annotations on public signatures. |

When a release ships, the maintainers concatenate each project's `Unshipped.txt` into `Shipped.txt` and empty `Unshipped.txt`. Don't do this yourself — it's part of the release process.

## Commit style

Conventional-commits-ish: `feat(area):`, `fix(area):`, `docs(area):`, `chore(area):`, `test(area):`, `refactor(area):`. Areas map to top-level folders or `src/` project names without the `Vais.Agents.` prefix (e.g. `feat(core):`, `docs(graph):`, `fix(runtime-host):`).

Keep commit bodies honest about intent — this is a library, not a product. PRs reference the issue that motivated them (`Closes #123` / `Refs #456`). Breaking changes are called out in the PR description with migration guidance.

`CHANGELOG.md` is updated **in the same commit** as the change, under `## [Unreleased]`. Use [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) categories: `Added` / `Changed` / `Fixed` / `Removed` / `Deprecated` / `Security`.

## Code of conduct

We follow the [Contributor Covenant](CODE_OF_CONDUCT.md).
