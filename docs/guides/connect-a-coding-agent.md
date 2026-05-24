# Guide: connect a coding agent (read-only)

Connect an external coding agent — Claude Code, OpenCode, or Codex — to a running Vais.Agents runtime so it can discover resources, inspect their schemas, and dry-run-validate candidate manifests. This is **read-only**: the agent can list, get, describe, diff, and validate resources but cannot apply or delete anything.

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

Supported kinds: `Agent`, `AgentGraph`, `McpServer`, `LlmGatewayConfig`, `McpGatewayConfig`, `ContainerPlugin`, `EvalSuite`.

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

## Authentication

By default, the design MCP endpoint has **no auth** when `VAIS_JWT_AUTHORITY` is not set in the runtime (localhost development mode).

When `VAIS_JWT_AUTHORITY` is set, the endpoint requires a JWT bearer token. Pass `--token <token>` to `npx @vais/connect`. This is a **read-only** gate; per-kind RBAC is deferred to Plan B.

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
