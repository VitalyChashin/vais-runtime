# AI assistants working in Vais.Agents

This page is a **vendor-neutral tooling guide** for AI coding assistants contributing to
`Vais.Agents`. It describes *categories* of MCP (Model Context Protocol) integrations and
command-shaped skills that make work in this repo fast and safe — it does not prescribe
a specific assistant brand, folder convention, or config file format.

Repo-invariant rules live in [`../../AGENTS.md`](../../AGENTS.md). Human contributor
workflow lives in [`../../CONTRIBUTING.md`](../../CONTRIBUTING.md). This page sits between
the two and answers: *"I am (or my AI assistant is) about to work on this repo — what
external tools should be wired in, and what repetitive workflows are worth capturing as
reusable commands?"*

---

## Scope & non-scope

**In scope.**

- Categories of MCP integrations that help with .NET / Orleans / Kubernetes / OPA / NuGet
  / GitHub / documentation-lookup workflows.
- Skill patterns (parameterised, scriptable recipes) for the repetitive operations this
  repo produces — build, test, pack, public-API promotion, preview-tag creation, sample
  scaffolding, ADR authoring, deferred-backlog updates.
- Guardrails that keep assistants out of trouble: what *not* to do autonomously.

**Out of scope.**

- A specific assistant's folder convention. `.claude/`, `.cursor/`, `.windsurfrules`,
  `.kiro/`, `.opencode.json`, `GEMINI.md`, `.mcp.json`, etc. are vendor-specific and belong
  in the vendor's own layout. This repo does not commit any of them.
- Recommending one assistant over another. Every modern coding assistant that respects
  `AGENTS.md` can work here.
- Secrets management for MCP credentials. That is a host / user concern — store OAuth
  tokens, API keys, and cluster kubeconfigs in the vendor's own secret store, never in
  this repo.

---

## MCP integrations by category

Grouped by problem the integration solves. If your assistant supports MCP servers, pick
one per category as needed; if it doesn't, fall back to the native tool (`gh`, `kubectl`,
`helm`, a web search).

### 1. Source forge (GitHub / GitLab / Gitea)

**Why it helps.** Opening, triaging, and cross-referencing issues, PRs, releases, and
discussions without leaving the editor. Essential once the repo is on a public forge and
issue-driven contribution begins.

**Typical operations for this repo:**
- Look up issues / PRs by ID or label (`plan`, `defer`, `pillar-*`).
- Create issues from `docs/roadmap/deferred-backlog.md` entries when a Phase 4 triage
  converts them into tracked work.
- Check CI run status for a branch.
- Read comments on a PR during code review.

**Good fit signals:** the MCP exposes at least `list_issues`, `get_pr`, `list_checks`,
`create_issue`. Bonus: `get_file_at_commit` for history-aware diffs.

**When not to use:** for local-only operations. If you're just reading a file that's
already in the working tree, use the filesystem / editor — not an API round-trip.

---

### 2. Documentation lookup (Context7, docs2md, web-fetch)

**Why it helps.** This repo consumes several fast-moving upstream libraries whose public
API shifts between minor versions. Copying a stale snippet from an old blog post is the
fastest way to get `TreatWarningsAsErrors` to reject your PR.

**Libraries you will look up most often:**
- Semantic Kernel (`Microsoft.SemanticKernel`, 1.74+).
- Microsoft Agent Framework (`Microsoft.Agents.AI`).
- Microsoft.Extensions.AI (MEAI, 10.x).
- Orleans (`Microsoft.Orleans.*`, 10.x — serializer attributes, grain semantics,
  TestingHost).
- Model Context Protocol SDK (`Microsoft.Mcp.*`).
- A2A SDK (`a2a-net`, currently `1.0.0-preview2`).
- KubeOps (`KubeOps.Operator`, 10.3.x — CRD transpiler quirks).
- OPA / Rego.
- OpenTelemetry .NET semantic conventions (GenAI).
- Polly (v8 resilience pipelines).

**Good fit signals:** the MCP fetches versioned documentation by package id + version,
not "latest docs everywhere". Version drift is the main failure mode these MCPs prevent.

**Alternative:** plain web fetch plus the upstream repo's `/docs/` directory. Slower but
always current.

---

### 3. Code-graph / semantic search

**Why it helps.** `Vais.Agents.sln` covers 27 library packages + 29 projects + ~45 test
projects. A string-grep across the workspace is noisy; a symbol-graph lookup
(callers-of / callees-of / who-implements-this-interface) is usually what you actually
wanted.

**Typical operations for this repo:**
- "Who calls `IAgentLifecycleManager.CreateAsync`?"
- "What implements `ICompletionProvider`?"
- "Which tests exercise `AgentManifestTranslator.TranslateAsync`?"
- "What would break if I rename `StatefulAgentOptions.Agent`?"

**Good fit signals:** the MCP parses C# (Roslyn or a Tree-sitter equivalent), exposes a
graph query API (`callers_of`, `callees_of`, `implementers_of`, `tests_for`), and caches
incrementally so warm queries are sub-second.

**When not to use:** for plain text searches where a symbol doesn't exist yet (grepping
for a string constant in YAML, a log message, an inline Rego rule). Use ripgrep / the
editor's find-in-files for that.

---

### 4. Kubernetes / cluster introspection

**Why it helps.** Phase 3 produced a Helm chart + CRD + operator. Reading a
live cluster's state (pod logs, CR status, events) during integration work beats
shell-exec of `kubectl` commands for every lookup.

**Typical operations for this repo:**
- `kubectl describe agent/<id>` equivalent — inspect an `Agent` CR's status.
- Tail runtime pod logs.
- Diff a rendered Helm template against what the cluster actually has.

**Good fit signals:** the MCP speaks the core Kubernetes API (`get`, `describe`,
`logs`, `apply --dry-run`) and respects kubeconfig context isolation.

**When not to use:** against production clusters from a coding session. Dev clusters
(kind, k3d, a disposable minikube) only. The `deploy/compose/` recipes cover most local
flows without needing a cluster at all.

---

### 5. Docker / container introspection

**Why it helps.** The runtime ships as a Docker image. Tasks like "what's the final
image size?" / "which layer pulled in that dependency?" / "does `docker compose -f
localhost.yml -f opa.yml config` validate?" are faster with an MCP than with manual CLI
plumbing.

**Typical operations for this repo:**
- Build the runtime image from `src/Vais.Agents.Runtime.Host/Dockerfile`.
- Validate compose overlays combine cleanly (`docker compose ... config --quiet`).
- Tail runtime container logs during a sample run.

**When not to use:** the assistant should **not** call `docker compose up` or `docker
run` autonomously (see §Etiquette below — servers are user-initiated). Validation
(`config`, `build --target`, image-size queries) is fine.

---

### 6. NuGet / package-registry lookup

**Why it helps.** Central Package Management lives in
[`../../Directory.Packages.props`](../../Directory.Packages.props). Every version bump
needs a sanity check against the registry: does this version exist? is there a newer
stable? are there known CVEs?

**Typical operations for this repo:**
- Resolve "latest stable" for a given package id.
- Confirm a pre-release version is still the latest pre-release (vs. superseded by a
  newer pre-release the comment in CPM warned us about).
- Scan `Directory.Packages.props` for versions that have moved upstream.

**Alternative:** `dotnet list package --outdated` against a temporary scratch project
that references all CPM packages, or the NuGet website.

---

### 7. Web search (general)

**Why it helps.** For error messages, library idioms, and "what's the current best
practice for X in .NET 9". Use it before guessing — the repo is new and many decisions
reference patterns from `microsoft/semantic-kernel`, `microsoft/agent-framework`,
`dotnet/orleans`, `open-policy-agent/opa`, and the MCP and A2A reference servers. Those
upstreams evolve faster than this repo does.

---

### 8. Integrations that are **not** useful for this repo

If you have any of these wired in globally, disable or ignore them while working here:

- Email, calendar, cloud drive (Gmail / Google Calendar / Google Drive / Outlook /
  Dropbox). This repo's work product is code and docs, not correspondence or meeting
  scheduling.
- Diagram / whiteboard tools (Miro, draw.io) — unless you are specifically authoring a
  diagram for an ADR, and even then the output goes into `docs/adr/` as an ASCII / Mermaid
  block, not a live embed.
- Chat platforms (Slack, Discord). Any collaboration happens through the forge (§1).

These tools are valuable in other contexts; they are noise here and using them costs
context window for zero repo value.

---

## Skill patterns (repetitive-workflow recipes)

Pitch these as command-shaped skills in whatever your assistant calls them (slash
commands, macros, workflows, task templates). Names below are suggestions — the point is
the recipe, not the exact invocation.

### `/build` — compile the solution or one project

**When:** every time you change C# code. `TreatWarningsAsErrors` means a warning breaks
the build, so you get immediate feedback on analyzer violations.

**Recipe.** Accept an optional `<project>`:

```bash
dotnet build Vais.Agents.sln --configuration Release --no-restore
# or
dotnet build src/<project>/<project>.csproj --configuration Release --no-restore
```

**Report:** first 20 errors + the build summary line. Group repeated errors (same
diagnostic id, many sites) as "CA1234 × 14 in project Foo".

---

### `/test` — run tests, optionally filtered

**When:** after any non-trivial change, and always before proposing a commit.

**Recipe.** Accept an optional `<project>` and `<filter>`:

```bash
dotnet test Vais.Agents.sln --configuration Release --no-build
# or
dotnet test tests/<project>.Tests/<project>.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~<class-or-keyword>
```

**Exclude live-LLM tests** (they need `OPENAI_API_KEY`):
```bash
dotnet test ... --filter "FullyQualifiedName!~LiveLlm"
```

**Exclude container tests** (Redis / Postgres / K8s):
```bash
dotnet test ... --filter "FullyQualifiedName!~Container"
```

**Report:** passed / failed count + first failure's assertion trace. Never summarise as
"tests pass" without reporting the count.

---

### `/pack-local` — build preview NuGets for samples to consume

**When:** before running samples that consume `Vais.Agents.*` via `PackageReference` from
the local feed.

**Recipe.**

```bash
dotnet pack Vais.Agents.sln --configuration Release --output artifacts/packages
```

**Report:** count of `.nupkg` + `.snupkg` files produced; package version.

---

### `/publicapi-promote` — milestone-time Unshipped → Shipped

**When:** creating an annotated preview tag. Never during normal development.

**Recipe.** For every project under `src/` that has a `PublicAPI.Unshipped.txt`:

1. Append the non-header lines of `PublicAPI.Unshipped.txt` to
   `PublicAPI.Shipped.txt`.
2. Reset `PublicAPI.Unshipped.txt` to just its `#nullable enable` header (and any
   `*REMOVED*` entries you want to retain for the release changelog — typically none).
3. Re-sort `PublicAPI.Shipped.txt` alphabetically within each group.
4. Re-run `dotnet build`. If it's clean, the promotion was correct.

**Report:** number of projects touched + total entries promoted.

---

### `/tag-preview` — create the annotated `v0.X.0-preview` tag

**When:** at the end of a pillar, after `/publicapi-promote` + `/build` + `/test` are all
green.

**Recipe.**

1. Identify the commit to tag (usually `HEAD` on the merge branch after the pillar's
   two-or-three-commit bundle has landed).
2. Draft a tag message summarising the pillar + PRs + key decisions + explicit
   "Deferred to next pillar" bullets. Model on the most recent entry in
   [`../../CHANGELOG.md`](../../CHANGELOG.md).
3. Create the tag locally: `git tag -a v0.X.0-preview <sha> -m "<message>"`.
4. **Do not push** the tag unless the user explicitly asks. Tags are close to immutable;
   a pushed mistake is expensive.
5. Update each deferred item's destination: add it to
   [`../roadmap/deferred-backlog.md`](../roadmap/deferred-backlog.md) under the right
   theme.

**Report:** tag name + commit SHA + CHANGELOG entry link. Confirm tag was not pushed.

---

### `/sample-new` — scaffold a new sample

**When:** adding a runnable demonstration of a shipped feature.

**Recipe.** Create `samples/<Name>/` containing:

- `<Name>.csproj` — .NET 9 console app, opts out of CPM
  (`<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`), references
  `Vais.Agents.*` via `PackageReference` against the local `artifacts/packages/` feed.
- `Program.cs` — the demo itself. Prefer a deterministic scripted fake provider over a
  live API call unless the sample is specifically about the live call.
- `README.md` — scenario, prerequisites, run command, expected output.
- Row in [`../../samples/README.md`](../../samples/README.md) — name, pillar, packages
  used, LoC, API-key requirement, related doc.

**Report:** the new row, a verification of `/pack-local` + `dotnet run --project
samples/<Name>` succeeding.

---

### `/adr-new` — start a new architecture decision record

**When:** a non-obvious design choice has alternatives worth capturing.

**Recipe.** Create `docs/adr/<NNNN>-<kebab-case-title>.md`, numbered to the next
available ADR id (currently `0004` is the last; the next is `0005`). Use the standard
template:

```markdown
# <NNNN>. <Title>

Status: **Proposed** | **Accepted** | **Superseded by ADR-XXXX** | **Deprecated**
Date: YYYY-MM-DD

## Context
<the forces that led to this decision>

## Decision
<what we chose>

## Alternatives considered
<what we rejected and why>

## Consequences
<what this forces downstream; what it forecloses>
```

**Report:** new ADR file path + a note on whether the concept page it relates to needs
an update to link out to this ADR.

---

### `/docs-lint` — verify Markdown links

**When:** after any docs change, before committing.

**Recipe.** For every `.md` file under `docs/`:

1. Extract relative `[text](path)` links.
2. Resolve each against the file's directory.
3. Report links that don't resolve to a file in the working tree.

A simple ripgrep-based walker is enough (no MCP required). External links are out of
scope here — consistency of *relative* links is the goal.

---

### `/deferred` — append an entry to the deferred backlog

**When:** any PR or milestone defers work. Per
[`../../AGENTS.md`](../../AGENTS.md) §Agent etiquette rule 8, deferrals go into
[`../roadmap/deferred-backlog.md`](../roadmap/deferred-backlog.md).

**Recipe.** Open the backlog, find the matching theme (or add a new one if none fits),
append a bullet:

```markdown
- **<one-line item>.** Next step: <concrete follow-up>.
```

**Report:** the theme hit + the one-line entry added.

---

## Agent etiquette (mirrored from AGENTS.md)

These are repeated here because they matter enough to restate — see
[`../../AGENTS.md`](../../AGENTS.md) §Agent etiquette for the canonical list. Any tool
or skill you wire up must respect them:

1. **Read files before editing them.** Edit against actual content, not expected
   content.
2. **Prefer `Edit` / patch tools over full-file rewrites.** Preserve whitespace, line
   endings, and trailing newlines.
3. **Do not start long-lived processes autonomously.** `dotnet run`, `docker compose
   up`, `helm install`, `kubectl apply` on anything except the user's dev cluster — all
   user-initiated. Offer the command instead.
4. **No `--no-verify` and no `--amend` of pushed commits.** Ever.
5. **No destructive git operations** (`reset --hard`, `push --force`, branch / tag
   deletion) without explicit user authorisation for that specific operation.
6. **Public API analyzer is authoritative.** If RS0016 / RS0017 / RS0025 / RS0026 /
   RS0037 fires, the fix is to update `PublicAPI.Unshipped.txt` — not to edit
   `PublicAPI.Shipped.txt` or to suppress the diagnostic.
7. **Every new public type needs XML docs.** CI will fail without them.
8. **Ask before scope-expanding.** A one-line fix should not balloon into a refactor
   unless the user asked for the refactor.
9. **Record deferred work.** Use the `/deferred` skill above.

---

## How to extend this page

Add your own skill patterns and MCP recommendations the same way you'd add a section to
any docs page:

1. Pick a category that already exists or add a new H3 if none fits.
2. Describe *why* the tool or skill helps for this specific repo (not in general).
3. Include a concrete recipe. Prefer an exact command over a prose description.
4. Note when **not** to use it — every tool has a failure mode worth flagging.
5. Keep it vendor-neutral. If a tip only works in one assistant, mention that
   explicitly and suggest the nearest equivalent for others.

If a skill becomes load-bearing for every PR, consider promoting its recipe into a CI
check instead — the goal is to make humans and assistants both successful, not to create
a parallel tooling stack that only assistants rely on.
