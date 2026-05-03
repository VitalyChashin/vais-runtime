# Vais Workbench — quickstart

Vais Workbench is an Electron desktop app for managing Vais.Agents resources on a running runtime. It connects to the HTTP control plane and lets you browse, inspect, deploy, validate, delete, and test resources without writing shell commands.

## Prerequisites

- Node.js 20 or later (`node --version`)
- A running `vais-agents-runtime` instance (default: `http://localhost:5000`)

See [Install the runtime locally](../../docs/guides/install-the-runtime-locally.md) for a one-command docker-compose recipe.

## Install dependencies (first run only)

```bash
cd workbench
npm install
```

> The first `npm install` downloads the Electron binary (~180 MB). On slow connections this can take a few minutes.

## Run in development mode

```bash
npm run dev
```

A single command starts the Vite dev server and launches the Electron shell. Hot-module replacement is active for renderer changes; main-process changes require a restart.

## Build a distributable

```bash
npm run build
```

Produces a packaged app in `dist-electron/` (see `electron-builder.config.ts` for platform targets).

## Run unit tests

```bash
npm test
```

Runs Vitest in Node mode (no Electron). Tests cover the API client.

---

## What you get

### Connection bar

The toolbar at the top shows the active connection. Click the dropdown to switch between named connections defined in `~/.vais/workbench/config.yaml`. The selected connection is saved and restored on next launch.

### Resource tree

The left sidebar lists all five resource kinds:

| Kind | Control-plane endpoint |
|---|---|
| Agents | `/v1/agents` |
| Graphs | `/v1/graphs` |
| LLM Gateways | `/v1/llm-gateways` |
| MCP Gateways | `/v1/mcp-gateways` |
| MCP Servers | `/v1/mcp-servers` |

Each section polls every 5 seconds. Click any resource to select it.

### Detail pane — YAML tab

Shows the selected resource's manifest as read-only YAML.

- **Edit** — opens the Deploy pane pre-filled with the current YAML for editing and re-applying.
- **Delete** — opens a confirmation dialog; confirms before calling `DELETE /v1/<kind>/{id}`.

### Detail pane — References tab

- **Outbound** — lists `llmGatewayRef`, `mcpGatewayRef`, and `mcpServers` entries for the selected resource. Each entry is a clickable link that navigates to the referenced resource.
- **Referenced by** — lists any agents or graphs that point at the selected resource. Useful for checking blast radius before deleting a shared gateway.

### Detail pane — Test tab

For **agents** and **graphs**: send a message and see the streamed response. The last 5 runs are shown in reverse-chronological order.

For **llm-gateways**, **mcp-gateways**, **mcp-servers**: a stub panel noting that direct probe endpoints are not yet available.

### Deploy pane

Click **Deploy** in the top-right corner to open the YAML editor.

1. Paste or type one or more YAML documents (separated by `---`).
2. The `kind:` field is auto-detected. Supported values: `Agent`, `AgentGraph`, `LlmGatewayConfig`, `McpGatewayConfig`, `McpServer`.
3. Click **Validate** to run `POST /v1/<kind>/validate` and see any errors inline.
4. Click **Apply** to create or update the resource(s). Multi-document pastes are applied in dependency order (gateways and servers before agents and graphs).

---

## Config file

`~/.vais/workbench/config.yaml` is auto-created on first launch:

```yaml
connections:
  - name: localhost
    baseUrl: http://localhost:5000
activeConnection: localhost
```

Add more entries under `connections` to switch between runtimes. No auth fields in v1 — all connections are unauthenticated.

---

## Keyboard shortcuts

| Action | Shortcut |
|---|---|
| Send message in Test tab | Enter |
| Insert newline in Test tab | Shift+Enter |
| Reload app (dev) | F5 / Cmd+R |
| Open DevTools (dev) | F12 / Cmd+Option+I |
