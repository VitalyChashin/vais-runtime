# v0.15 CLI — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.15-cli-spike.md`](./actor-agents-oss-v0.15-cli-spike.md). Answers Q1–Q5 with evidence. Landing verdict at the bottom.

Created 2026-04-20. **Status**: complete. Q1–Q5 resolved from framework audit + HTTP-client verb-table map + SSE-attach sketch + config schema draft + dotnet-tool packaging verification.

---

## Q1 — Framework

### Spectre.Console.Cli 0.55.0 confirmed

NuGet audit (2026-04-20): latest stable **0.55.0**, targets `netstandard2.0` / `net8.0` / `net9.0` / `net10.0`. Apache 2.0. Transitive dep closure: `Spectre.Console` (same package family) + nothing else heavy.

### Prototype: `vais version` — Spectre vs. System.CommandLine

**Spectre.Console.Cli (~15 lines):**

```csharp
public sealed class VersionCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var version = typeof(VersionCommand).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        AnsiConsole.MarkupLine($"[bold]vais[/] [grey]v{version}[/]");
        return 0;
    }
}

// In Program.cs:
var app = new CommandApp();
app.Configure(c => c.AddCommand<VersionCommand>("version"));
return app.Run(args);
```

**System.CommandLine (~18 lines):**

```csharp
var versionCommand = new Command("version", "Print CLI version");
versionCommand.SetHandler(() =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    Console.WriteLine($"vais v{version}");
});
var root = new RootCommand { versionCommand };
return await root.InvokeAsync(args);
```

### Decision (Q1): **Spectre.Console.Cli 0.55.0**

LoC comparable, but Spectre's `IAnsiConsole` gives coloured / table / panel output across every command essentially for free. For verbs like `vais get agents` (table render) and `vais logs` (coloured event prefixes), the rendering story matters. Add a ~200KB dep for meaningful UX wins.

---

## Q2 — Verb set + shape

### Full verb table

| Subcommand | Client method | Flags | Notes |
|---|---|---|---|
| `vais apply -f <file>` | `CreateAsync` or `UpdateAsync` | `-f`/`--file`, `--idempotency-key`, `-o json\|yaml\|table` | Loader picks JSON vs. YAML by extension; server-side-apply via create-or-update dispatch based on existing version |
| `vais get agents` | `ListAsync(labelPrefix, limit)` | `--label-prefix`, `--limit`, `-o json\|yaml\|table` | Table default: id / version / description / labels |
| `vais get agents <name>` | `QueryAsync(id, version)` | `--version`, `-o json\|yaml\|table` | Single-agent detail; pretty-prints manifest |
| `vais invoke <id>` | `InvokeAsync` or `InvokeStreamEventsAsync` | `--text`, `--session`, `--stream`, `--version`, `--idempotency-key`, `-o text\|json` | `--stream` routes to SSE; default unary |
| `vais logs <id>` | `InvokeStreamEventsAsync` | `--session`, `--only <event-kinds>`, `--since <ts>` | Live-run SSE attach (Q3) |
| `vais signal <id>` | `SignalAsync` | `--kind`, `--payload`, `--version` | `--payload` accepts inline JSON or `@file.json` |
| `vais delete <id>` | `EvictAsync` | `--version`, `--force`, `--idempotency-key` | Prompts confirm unless `--force` |
| `vais cancel <id>` | `CancelAsync` | `--version`, `--idempotency-key` | Not destructive; no prompt |
| `vais config get-contexts` | — (local) | `-o json\|yaml\|table` | Lists known contexts from config file |
| `vais config current-context` | — (local) | none | Prints active context name |
| `vais config use-context <name>` | — (local) | none | Switches `currentContext` in config |
| `vais config set-context <name>` | — (local) | `--server`, `--token`, `--cluster`, `--user` | Adds or updates a context |
| `vais init <name>` | — (scaffold) | `-o <file>`, `--model <provider>`, `--mode toolCalling\|sgr` | Emits a starter YAML manifest |
| `vais version` | — | none | Prints CLI + shipped-client version |

14 verbs total (5 config subcommands merged into 4 ops + 10 top-level verbs).

### Decision (Q2): **full lifecycle parity + SSE attach + config subcommand + init scaffold**

No gaps in `IAgentControlPlaneClient` — every CLI verb maps to a single client call or a local config-file mutation. `invoke` doubles as unary (`InvokeAsync`) + streaming (`InvokeStreamEventsAsync`) via `--stream` flag.

---

## Q3 — `vais logs` semantics

### Three candidates audited

| option | how | wire cost | blocker |
|---|---|---|---|
| (a) Live-run SSE attach | `POST /v1/agents/{id}/invoke/stream` session-attached | zero new | None — reuses v0.12 |
| (b) Audit log | `GET /v1/audit` — doesn't exist | +1 new endpoint | Needs audit-log projection surface in runtime |
| (c) Journal replay | `GET /v1/agents/{id}/runs/{runId}/journal` — doesn't exist | +2 new endpoints | Needs run-registry (same blocker as AgentRun CRD v0.16+) |

### SSE attach sketch

```csharp
public sealed class LogsCommand : AsyncCommand<LogsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, LogsSettings settings)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var client = _clientFactory.Create(settings.Context);
        var request = new AgentInvocationRequest(
            Text: settings.Attach ?? string.Empty,
            SessionId: settings.SessionId);

        try
        {
            await foreach (var evt in client.InvokeStreamEventsAsync(
                settings.AgentId, request, version: null, idempotencyKey: null, cts.Token))
            {
                if (settings.OnlyKinds is { Count: > 0 } && !settings.OnlyKinds.Contains(evt.GetType().Name))
                    continue;
                Render(evt);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]interrupted[/]");
            return 130; // SIGINT exit code convention
        }
    }
}
```

Event rendering uses Spectre colour:
- `TurnStarted` / `TurnCompleted` — `[green]`
- `TurnFailed` / `GuardrailTriggered` — `[red]`
- `CompletionDelta` — plain text (accumulated as assistant turn)
- `ToolCallStarted` / `ToolCallCompleted` — `[blue]`
- `InterruptRaised` — `[yellow]`

### Decision (Q3): **(a) live-run SSE attach**

Zero new runtime surface. `vais logs <agentId> [--session <id>] [--only <kinds>] [--since <ts>]` opens an SSE stream and prints events with colour + kind prefix. Ctrl-C sends cancellation, client exits with code 130 (POSIX SIGINT). Audit + journal-replay deferred to v0.16+ alongside a shipped run-registry endpoint.

### Note on `--since`

v0.12 SSE doesn't support replay from a timestamp (no `Last-Event-Id` support; documented non-goal). `--since` on v0.15 silently filters client-side by `AgentEvent.At >= --since`. Consumers who need durable replay wait for the run-registry pillar.

---

## Q4 — Config + auth

### Config file schema (locked)

```yaml
apiVersion: vais.io/v1
kind: Config
currentContext: default
clusters:
  - name: local
    server: http://localhost:5080
  - name: prod
    server: https://vais.example.invalid
users:
  - name: dev-token
    token: "<jwt>"
  - name: prod-token
    tokenFile: /var/run/secrets/vais/token
contexts:
  - name: default
    cluster: local
    user: dev-token
  - name: production
    cluster: prod
    user: prod-token
```

Shape mirrors `kubectl` — `{clusters, users, contexts, currentContext}`. `tokenFile` reads from disk on each invocation (supports rotated tokens). Extension field `apiVersion: vais.io/v1` gates future evolution.

### Cross-platform path resolution

```csharp
internal static string ResolveConfigPath()
{
    var envOverride = Environment.GetEnvironmentVariable("VAIS_CONFIG");
    if (!string.IsNullOrWhiteSpace(envOverride))
        return envOverride;
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".vais", "config.yaml");
}
```

Verified: `Environment.SpecialFolder.UserProfile` resolves to `/home/<user>` on Linux, `/Users/<user>` on macOS, `C:\Users\<user>` on Windows. Single well-known path works everywhere.

### Token precedence

1. `--token <jwt>` flag (explicit per-command override).
2. `VAIS_TOKEN` env var.
3. Active context's `users.<name>.token` or file from `users.<name>.tokenFile`.
4. None → unauthenticated (CLI prints warning; server decides).

### Decision (Q4): **kubectl-shape contexts + static token + env override**

- Config at `~/.vais/config.yaml` with `kubectl`-shape `{clusters, users, contexts, currentContext}`.
- `VAIS_CONFIG` env var overrides path.
- Auth: static `token` or `tokenFile` on the user record; `VAIS_TOKEN` env overrides; `--token` flag wins.
- OIDC device-flow + K8s SA projected-token + kubectl-style exec plugin all deferred to v0.15.1+.

---

## Q5 — Package shape + testing

### dotnet-tool packaging

`csproj` settings:

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>vais</ToolCommandName>
  <RootNamespace>Vais.Agents.Cli</RootNamespace>
  <AssemblyName>Vais.Agents.Cli</AssemblyName>
  <OutputType>Exe</OutputType>
  <!-- standard Description + PackageTags -->
</PropertyGroup>
```

Produces a nupkg consumable via `dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview`. Invoked as `vais <subcommand>`.

### Dep closure (measured)

- `Vais.Agents.Control.Http.Client` (project ref) → pulls Abstractions, Control.Abstractions, System.Net.Http, System.Net.ServerSentEvents.
- `Vais.Agents.Control.Manifests.Json` (project ref).
- `Vais.Agents.Control.Manifests.Yaml` (project ref) → pulls YamlDotNet.
- `Spectre.Console.Cli 0.55.0` → pulls Spectre.Console.
- `Microsoft.Extensions.Http` (already transitively).
- No new CPM entries needed beyond `Spectre.Console.Cli` + `Spectre.Console`.

Packed size target: ~2-3MB nupkg.

### Spectre `CommandAppTester` for unit tests

```csharp
var app = new CommandApp<ApplyCommand>();
app.Configure(c => c.Settings.Registrar.RegisterInstance<IAgentControlPlaneClient>(mockClient));

var result = app.Run(new[] { "apply", "-f", "/tmp/agent.yaml" });
result.ExitCode.Should().Be(0);
result.Output.Should().Contain("agent.created");
```

### Decision (Q5): **single library + dotnet tool + unit-test project with Spectre's test harness**

New packages:
- **`Vais.Agents.Cli`** — library + dotnet tool. Package count 24 → 25.
- Test project **`Vais.Agents.Cli.Tests`** (IsPackable=false) — unit tests with mocked `IAgentControlPlaneClient`.
- No integration-test project; Http.Client already covers the wire.

---

## Verdict — ready to write the pillar plan

### Locked decisions

1. **Framework** = `Spectre.Console.Cli 0.55.0`.
2. **Verb set** = full 7-lifecycle-verb parity + streaming attach + config subcommand + `init` scaffold + `version`. 14 subcommands total (10 top-level + 4 config sub).
3. **`vais logs` semantic** = live-run SSE attach (reuses v0.12). Audit + journal-replay deferred to v0.16+.
4. **Config file** = `~/.vais/config.yaml` (kubectl-shape: `{clusters, users, contexts, currentContext}`). `VAIS_CONFIG` env override.
5. **Auth** = static token or `tokenFile` on user record + `VAIS_TOKEN` env + `--token` flag; precedence: flag > env > context user > unauthenticated.
6. **Package** = `Vais.Agents.Cli` dotnet tool (`<PackAsTool>true</PackAsTool>`, `<ToolCommandName>vais</ToolCommandName>`). Package count 24 → 25.
7. **Test project** = `Vais.Agents.Cli.Tests` using Spectre's `CommandAppTester` + mocked `IAgentControlPlaneClient`.
8. **Output formats** = `-o json|yaml|table` on `get`; `-o text|json` on `invoke`. Table default for `get` (kubectl idiom).
9. **Exit codes** = 0 success, 1 usage, 2 API error, 3 policy denied, 4 auth failure, 130 SIGINT. POSIX convention.
10. **Shell completion** = deferred. Spectre has basic support but the story is cross-shell-UX-heavy; polish pillar.

### Proposed PR shape (4 PRs)

**PR 1 — Package skeleton + config file + `vais version` + `vais config *`.**
- New csproj `Vais.Agents.Cli` with `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`.
- Public: `Program` entry point + `VaisCliConfig` data class (for testability). Everything else internal.
- Config file loader (`VaisConfigFile`) — reads/writes `~/.vais/config.yaml` with `VAIS_CONFIG` env override. Uses YamlDotNet.
- Commands: `version`, `config get-contexts`, `config current-context`, `config use-context`, `config set-context`.
- `Vais.Agents.Cli.Tests` project with Spectre test harness. ~8 unit tests.
- PublicAPI baseline.

**PR 2 — `apply` / `get` / `delete` / `cancel` / `init` / auth plumbing.**
- `ClientFactory` — resolves active context → builds `AgentControlPlaneClient` with bearer token from the precedence chain.
- `apply -f` (routes to YAML or JSON loader → `CreateAsync`/`UpdateAsync`).
- `get agents [name]` with `-o json|yaml|table` formatting.
- `delete <id>` with confirm prompt + `--force`.
- `cancel <id>`.
- `init <name>` scaffold (emits a starter YAML to stdout or `-o <file>`).
- ~15 unit tests via Spectre `CommandAppTester` with mocked client.

**PR 3 — `invoke` / `logs` / `signal` + streaming.**
- `invoke <id>` — unary + `--stream` variant.
- `logs <id>` — SSE attach with Ctrl-C cancellation + event-kind filter + colour output.
- `signal <id>` — JSON payload parsing + `@file.json` support.
- `SignalPayloadParser` helper.
- ~10 unit tests + ~3 streaming-specific tests (mocked SSE stream).

**PR 4 — v0.15.0-preview cut.**
- API freeze on the new package (Unshipped → Shipped).
- Pack 25 packages at `0.15.0-preview`.
- Smoketest bump + CLI library-surface probe (construct `VaisCliConfig`, round-trip the YAML config, assert commands register).
- Tag `v0.15.0-preview`.
- Milestone log + research doc §7 strike-through.

### Effort estimate

**4 PRs, ~2-2.5 days focused work.** Lighter than v0.13 (no Helm chart / Dockerfile) and slightly heavier than v0.14 (more surface area; 14 subcommands; config file + auth plumbing). Largest PR is PR 2 (apply + get + delete + cancel + init + auth).

### Non-goals for v0.15

- **Audit log query** (`vais audit`). Needs new runtime HTTP endpoint. → v0.16+.
- **Journal replay** (`vais logs --runId <id>`). Needs run-registry. → v0.16+ alongside AgentRun CRD.
- **OIDC device-flow auth** (`vais auth login`). Polish pillar.
- **K8s SA projected-token auth** (`users.<n>.exec: kubectl ...`). Kubectl-style exec plugin. Polish.
- **Shell completion** (bash/zsh/fish/powershell). Deferred.
- **`vais describe`** (kubectl-style detailed view). Subset of `get -o yaml` + pretty-print; polish.
- **`vais port-forward`-equivalent** (exposing agent over local port). Orthogonal.
- **`vais top`** (resource usage). Needs metrics endpoint. → polish pillar.
- **Standalone self-contained exe** (single-file publish). Consumers who want it do it themselves.

---

## Open items (for pillar planning, not blockers)

1. **Spectre.Console.Cli integration with `Microsoft.Extensions.DependencyInjection`** — Spectre's default registrar is its own type; there's a `Spectre.Console.Cli.Extensions.DependencyInjection` adapter. Lean: use Spectre's built-in `ITypeRegistrar` for simplicity; DI is overkill for a CLI of this size.
2. **YAML vs. JSON output format default** — kubectl defaults to YAML for single-resource, table for list. Lean: match kubectl (table for `get agents`, yaml for `get agents <name>`, table for `config get-contexts`).
3. **`vais delete` confirm prompt** — interactive prompt only when stdin is a TTY; when piped, auto-accept. Prevents script breakage. `IAnsiConsole.Confirm()` already has the `IsTerminal` check.
4. **`vais apply -f -`** reads from stdin. Documented; low cost.
5. **`vais invoke --text "@file"`** reads text from a file. Nice polish; ship it.
6. **`vais config set-context`** with `--insecure-skip-tls-verify` — for dev against self-signed certs. Lean: include; name mirrors kubectl.
7. **`vais version --check`** — optional remote check against NuGet for newer CLI versions. Deferred.
8. **Command aliases** — `vais ls` for `get agents`? `vais rm` for `delete`? Lean: skip; kubectl doesn't do aliases either.
9. **Error output on stderr vs. stdout** — errors + prompts on stderr; data on stdout. Standard POSIX. Spectre respects this via `AnsiConsole.Console.Profile.Out`.
10. **Logging verbosity flag** — `-v` / `--verbose` (HTTP request/response dump). Useful for debug; low cost; include.
