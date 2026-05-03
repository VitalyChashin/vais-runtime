# Changelog — Vais Workbench

All notable changes to the Workbench desktop app are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.2.0] — 2026-05-03

### Added

- **Dark theme design system.** Full visual redesign of all UI surfaces; Tailwind utility classes replaced with a BEM CSS architecture.
  - `src/tokens.css` (via `src/index.css`) — 16 semantic CSS custom properties (`--color-bg-base/surface/elevated/inset`, `--color-border`, `--color-text-primary/secondary/muted`, `--color-accent`, `--color-success/warn/error`) registered in both `:root` and Tailwind v4 `@theme`.
  - `src/chrome.css` — BEM component classes for the full app shell: `.app` (CSS grid layout), `.header`, `.sidebar`, `.section`/`.row` (collapsible resource tree), `.btn` variants (`--primary`, `--ghost`, `--bare`, `--danger`), `.tabs`/`.tab`, `.toolbar`, `.kind` badge, `.overlay`/`.modal` (shared for Deploy and Delete dialogs).
  - `src/monacoTheme.ts` — custom Monaco editor dark theme (`vais-dark`) using VS Code Dark+ token colors on the `bg-inset` surface; teal/cyan cursor and selection highlight; applied to both the YAML tab and Deploy pane editors.
  - `src/styles/testPanel.css` — local styles for `AgentTestPanel`: `.test`, `.run`, `.run__output`, `.run__cursor` (animated teal streaming cursor).
  - `src/styles/deleteDialog.css` — local styles for `DeleteDialog`: `.warn-callout` (amber warning panel with reverse-reference list).
- **Sidebar footer** — shows the active connection `baseUrl` and a green status indicator.
- **Per-kind section icons** — each resource kind in the sidebar has a distinct SVG icon (CPU for Agents, git-branch for Graphs, chip for LLM Gateways, server-rack for MCP Gateways/Servers).
- **Sidebar skeleton loading states** — shimmer animation on resource rows while TanStack Query is fetching.
- **YamlTab toolbar** — resource name, kind badge, and Edit/Delete icon-buttons above the Monaco editor (previously just icon-less text buttons in a plain flex row).
- **Reverse-reference warning in DeleteDialog** — lists agents and graphs that reference the resource being deleted; shown as an amber callout before the user confirms. (Completes WB-7 spec.)

---

## [0.1.0] — 2026-05-03

### Added

- **WB-1 — Project scaffold.** Electron 33 + Vite 6 + React 19 + TypeScript 5 project wired via `vite-plugin-electron/simple`. Single `npm run dev` command starts both the Vite dev server and the Electron shell with no race condition. `npm test` runs Vitest unit tests in a pure-Node environment (separate `vitest.config.ts`, no Electron plugin). Tailwind CSS v4 via `@tailwindcss/vite`.

- **WB-2 — Connection config.** Persistent connection list stored at `~/.vais/workbench/config.yaml` (auto-created on first run with a `localhost:5000` default). IPC bridge (`config:read` / `config:write`) connects the Electron main process to the React renderer via a typed `window.vais` API. `ConnectionBar` dropdown lets users switch between named connections; active selection survives app restart.

- **WB-3 — Typed API client.** `VaisClient` — a thin `fetch` wrapper with `get`, `post`, `delete`, and `stream` methods — covers all five control-plane resource kinds (`agents`, `graphs`, `llm-gateways`, `mcp-gateways`, `mcp-servers`). `resources.ts` exports per-kind typed helpers plus dispatch helpers (`getResource`, `createResource`, `validateResource`, `deleteResourceById`, `invokeResource`). 5 Vitest unit tests cover all HTTP verbs with `vi.stubGlobal('fetch', ...)`.

- **WB-4 — Resource tree + polling.** Collapsible `ResourceSection` components for all five resource kinds, each polling at 5 s via TanStack Query v5 `refetchInterval`. Count badge per section. Active item highlighted. Loading, error, and empty-list states handled inline. Selection stored in a Zustand `selectionStore`; clicking any item sets `kind` + `id`.

- **WB-5 — Detail pane (YAML + References tabs).** Split-tab pane mounted on the right of the resource tree.
  - **YAML tab** — read-only Monaco editor (`@monaco-editor/react`) showing the selected resource's manifest as YAML (`js-yaml`). Edit and Delete action buttons.
  - **References tab** — Outbound section lists `llmGatewayRef`, `mcpGatewayRef`, and `mcpServers[]` entries as clickable links that navigate to the referenced resource. Referenced-by section lists agents and graphs that reference the current resource. Both sections use the shared TanStack Query cache (no extra requests).

- **WB-6 — Deploy flow.** Modal overlay with an editable Monaco YAML editor.
  - Opened via the global **Deploy** button in the header (blank canvas) or **Edit** in the YAML tab (pre-filled with current manifest).
  - `kind:` field in the YAML is auto-detected; unknown kinds show an inline error.
  - **Validate** → `POST /v1/<kind>/validate` → inline pass/fail message.
  - **Apply** → `POST /v1/<kind>` for each document → TanStack Query cache invalidation for the affected kind(s).
  - Multi-document YAML (`---` separator) is split and applied in dependency order: `llm-gateways → mcp-gateways → mcp-servers → agents → graphs`.

- **WB-7 — Delete flow.** Confirmation dialog triggered by the **Delete** button in the YAML tab. Shows resource id and kind. On confirm: `DELETE /v1/<kind>/{id}` → cache invalidation → selection cleared.

- **WB-8 — Agent / AgentGraph test panel.** **Test** tab rendered for `agents` and `graphs` resources.
  - Textarea input; Enter sends, Shift+Enter inserts a newline.
  - `POST /v1/<kind>/{id}/invoke` with `{ message }` body.
  - Response consumed as a `ReadableStream`; chunks appended incrementally with an animated cursor while streaming. Non-streaming (single-JSON) responses displayed whole.
  - Last 5 runs kept in component state (not persisted).

- **WB-9 — Stub test panels.** **Test** tab for `llm-gateways`, `mcp-gateways`, and `mcp-servers` renders a `ProbeStub` component: disabled Probe button with a note that the probe endpoint is not yet available.

- **WB-10 — Plugin extension point.** In-process panel plugin system.
  - Plugins are CommonJS `.js` files dropped into `~/.vais/workbench/plugins/` (directory auto-created).
  - Each plugin exports `{ kind, tabLabel, render }` where `render(resource)` returns an HTML string.
  - Plugins are loaded via `plugins:load` IPC (main process `require()`s each file; `render` is serialised as `.toString()` for IPC transport; renderer reconstructs via `new Function`).
  - Matching plugin tabs appear after the built-in tabs on the resource detail pane.
  - `dangerouslySetInnerHTML` rendering is intentionally unguarded for v1 (local files only; see `docs/plugins.md §Security`).
