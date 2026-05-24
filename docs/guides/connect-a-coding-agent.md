# Guide: connect a coding agent

Connect an external coding agent — Claude Code, OpenCode, or Codex — to a running Vais.Agents runtime so it can discover resources, inspect their schemas, dry-run-validate candidate manifests, and — when authorized — **author** resources (apply / delete / run evals). By default the connection is **read-only**; mutation is opt-in and gated by RBAC + (for code-running kinds) human approval. See [Authoring (read-write)](#authoring-read-write).

Shipped in the design-tools MCP server (`Vais.Agents.Control.Mcp.Server`), accessible at `/design-mcp`.

---

## What the agent can do

Once connected, the coding agent has access to five MCP tools and seven per-kind ontology resources.

### Tools

| Tool | What it does |
|---|---|
| `vais.list` | List all registered resources of a given kind. Optional `labelSelector` prefix filter. |
| `vais.get` | Fetch a single resource by kind + name (+ optional version). |
| `vais.describe` | Return ontology info for a kind: field types, required fields, cross-reference edges, capability/risk tags, and authoring recipes. |
| `vais.diff` | Parse a candidate manifest and diff its spec against the currently registered version. No resource is modified. |
| `vais.validate` | Dry-run validate a candidate manifest: JSON-Schema check + dangling cross-reference check. Returns `{ ok, errors[], suggestions[] }`. No resource is created or modified. |

Mutating tools (shown only to a caller authorized to author — see [Authoring](#authoring-read-write)):

| Tool | What it does |
|---|---|
| `vais.apply` | Create or update a resource from a manifest. Validates first; returns `{ ok, kind, name, action }`, or `{ ok:false, errors, suggestions }`, or `{ ok:false, status:'pending-approval', requestId }` for a code-running kind awaiting approval, or `{ ok:false, denied:true }`. |
| `vais.delete` | Delete a resource by kind + name. |
| `vais.eval` | Run an eval suite to verify behavior — inline `suite` manifest or registered `suiteRef`. Returns `{ ok, runId }`. |
| `vais.eval.status` | Poll an eval run by `runId`. Returns status + per-case pass/fail counts. |

Supported kinds: `Agent`, `AgentGraph`, `McpServer`, `LlmGatewayConfig`, `McpGatewayConfig`, `ContainerPlugin`, `EvalSuite`. `vais.apply`/`vais.delete` cover the six manager-backed kinds (not `EvalSuite`/`Extension`).

### Ontology resources

Each kind is also exposed as an MCP resource at `vais-ontology://<Kind>` (e.g. `vais-ontology://Agent`). The resource returns the full ontology entry as JSON: required fields, field types, cross-reference edges, capability/risk tags, and authoring recipes.

---

## Prerequisites

- A running Vais.Agents runtime (v0.16+). See [QUICKSTART.md](../../QUICKSTART.md) or [install-the-runtime-locally.md](install-the-runtime-locally.md).
- Node.js 18+ on the machine where you run the agent.

---

## Step 1 — write the connection config

Run `npx @vais/connect` from the root of your project directory. It detects the active agent and writes the appropriate config file.

```bash
# No auth (localhost mode without JWT)
npx @vais/connect --url http://localhost:5000

# With a bearer token (when VAIS_JWT_AUTHORITY is set in the runtime)
npx @vais/connect --url http://localhost:5000 --token <bearer-token>
```

The tool auto-detects the agent type from environment variables. Override with `--agent` when needed:

```bash
npx @vais/connect --url http://localhost:5000 --agent opencode
```

### What gets written

| Agent | Config file | Entry written |
|---|---|---|
| Claude Code (default) | `.mcp.json` in project root | `mcpServers.vais-design` |
| OpenCode | `opencode.json` in project root | `mcp.vais-design` |
| Codex | `~/.codex/config.toml` | `[mcpServers.vais-design]` |
| Generic | stdout | JSON snippet for manual paste |

### Updating the entry

Re-run the same command (or use the `update` subcommand) to overwrite the connection entry. The rest of the config file is left unchanged.

```bash
npx @vais/connect update --url http://new-host:5000 --token <new-token>
```

---

## Step 2 — verify the connection

Start or restart your coding agent so it picks up the new config.

**Claude Code** — in a new session, the agent will list `vais-design` in its available MCP servers. Ask it to list your agents:

```
List all registered agents using the vais.list tool.
```

**OpenCode** — the `vais-design` server should appear in the tool list on the next session start.

**Check the tools are visible**

A healthy connection returns at minimum `vais.list`, `vais.get`, `vais.describe`, `vais.diff`, and `vais.validate` in the MCP `tools/list` response.

---

## Step 3 — example prompts

Once connected, the agent can help you author and validate manifests. Some starting points:

```
Describe the Agent kind — what fields are required and what cross-references does it support?
```

```
List all registered McpServer resources.
```

```
Validate this manifest:
{
  "apiVersion": "vais.agents/v1",
  "kind": "Agent",
  "metadata": { "id": "my-agent", "version": "1.0" },
  "spec": {
    "handler": { "typeName": "maf" },
    "mcpGatewayRef": "my-gateway"
  }
}
```

```
Diff this candidate Agent spec against the currently registered version — tell me what changed.
```

---

## Authoring (read-write)

Mutation is **opt-in** and defends in depth: bearer-token authn → per-kind RBAC by scope → human approval for code-running kinds → full audit. With none of it enabled (localhost dev), the agent can author freely; enable the layers below for a shared or externally-reachable runtime.

**1. RBAC — who can author what.** Author-roles live in the ontology overlay (`contracts/ontology/overlay.example.json` is the template), keyed by JWT scope string:

```jsonc
// authorRoles maps a JWT scope → per-kind permissions ("write" | "delete" | "*")
"authorRoles": { "roles": {
  "vais.author":       { "permissions": { "Agent": ["*"], "AgentGraph": ["*"], "McpServer": ["*"],
                                          "LlmGatewayConfig": ["*"], "McpGatewayConfig": ["*"], "EvalSuite": ["*"] } },
  "vais.plugin-admin": { "permissions": { "ContainerPlugin": ["*"], "Extension": ["*"] } },
  "vais.readonly":     { "permissions": {} },
  "vais.approver":     { "permissions": {} }
} }
```

Point the runtime at your overlay with `VAIS_ONTOLOGY_OVERLAY_PATH`. The agent's bearer token must carry the matching scopes (the `scope`/`scp` claim). A read-only token sees only the read tools in `tools/list`; `vais.apply` of a kind the caller can't write returns `{ ok:false, denied:true }`.

**2. Approval — human gate for code-running kinds.** Set `VAIS_APPROVALS_ENABLED=true`. Applying a `ContainerPlugin` or `Extension` (kinds that run code in the runtime) returns `{ ok:false, status:'pending-approval', requestId }` and mutates nothing. An operator approves the exact manifest:

```bash
vais approvals list                 # see pending requests
vais approvals approve <requestId>  # requires the vais.approver scope
```

The agent then re-applies the **same** manifest and it proceeds. A tampered manifest re-hashes and stays held.

**3. Audit.** Set `VAIS_AUDIT_LOG_PATH=/var/log/vais/audit.jsonl` to record every authoring call (allow / deny / approval) as JSON Lines.

The same RBAC + approval + audit applies to the REST control plane and the CLI — the MCP path is not a bypass.

---

## Authentication

By default, the design MCP endpoint has **no auth** when `VAIS_JWT_AUTHORITY` is not set in the runtime (localhost development mode).

When `VAIS_JWT_AUTHORITY` is set, the endpoint requires a JWT bearer token. Pass `--token <token>` to `npx @vais/connect`. Per-kind authoring authorization is driven by the token's scopes — see [Authoring](#authoring-read-write).

> **Security note:** listing all resources exposes the full resource topology of your runtime to the connected agent. This is intentional for a design-tools use case. In production, limit access via the bearer token to authorised agents only.

---

## Manual config (without npx)

If you prefer to write the config by hand, the entry for Claude Code's `.mcp.json` is:

```json
{
  "mcpServers": {
    "vais-design": {
      "type": "http",
      "url": "http://localhost:5000/design-mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

For OpenCode's `opencode.json`:

```json
{
  "mcp": {
    "vais-design": {
      "type": "remote",
      "url": "http://localhost:5000/design-mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

Omit the `headers` block when running without JWT auth.
