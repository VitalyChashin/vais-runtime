# Vais Workbench — panel plugins

The plugin system lets you add custom tabs to any resource detail pane without modifying the app source. Plugins are local CommonJS `.js` files loaded at startup.

## Plugin directory

```
~/.vais/workbench/plugins/
```

Created automatically on first launch. Drop any number of `.js` files here; they are all loaded on startup. Changes take effect after restarting the app.

## Plugin contract

Each file must export an object with three fields:

```js
module.exports = {
  kind: "agents",          // ResourceKind this tab appears on
  tabLabel: "My Tab",      // Label shown on the tab button
  render: (resource) => {  // Returns an HTML string (innerHTML)
    return "<pre>" + JSON.stringify(resource, null, 2) + "</pre>"
  }
}
```

### Fields

| Field | Type | Description |
|---|---|---|
| `kind` | `string` | The `ResourceKind` value (`"agents"`, `"graphs"`, `"llm-gateways"`, `"mcp-gateways"`, `"mcp-servers"`). The tab only appears on resources of this kind. |
| `tabLabel` | `string` | Text shown on the tab button. Must be unique across plugins for the same `kind`. |
| `render` | `(resource: unknown) => string` | Called with the resource manifest object; must return an HTML string. Errors thrown here are caught and displayed as a red error message in the tab. |

The `resource` argument is the parsed JSON/YAML manifest — the same object shown in the YAML tab. Shape varies by kind; cast it as needed.

## Example — raw JSON viewer

```js
// ~/.vais/workbench/plugins/json-viewer.js
module.exports = {
  kind: "agents",
  tabLabel: "JSON",
  render(resource) {
    const json = JSON.stringify(resource, null, 2)
    return `<pre style="font-size:12px;white-space:pre-wrap;word-break:break-all">${json}</pre>`
  }
}
```

## Example — show llmGatewayRef badge

```js
// ~/.vais/workbench/plugins/gateway-badge.js
module.exports = {
  kind: "agents",
  tabLabel: "Gateway",
  render(resource) {
    const ref = resource.llmGatewayRef
    if (!ref) return '<p style="color:#888">No LLM gateway bound.</p>'
    return `<div style="padding:8px;border:1px solid #3b82f6;border-radius:4px;color:#1d4ed8">
      LLM Gateway: <strong>${ref}</strong>
    </div>`
  }
}
```

## Loading order

Files are loaded in filesystem order (typically alphabetical). If two plugins for the same `kind` share a `tabLabel`, both tabs appear — avoid duplicate labels to prevent confusion.

The `require` cache is cleared between loads so the same file can be edited and reloaded by restarting the app.

## Security

> **v1 limitation.** The `render` function is reconstructed via `new Function` in the renderer process and its output is injected as `innerHTML` (`dangerouslySetInnerHTML`). This is intentionally unguarded for the v1 in-process model where plugins are local files authored by the person running the app.
>
> Do not load plugins from untrusted sources. A future version will sandbox plugin execution in an isolated WebView renderer. Tracked in `research/vais-agents-management-ui-2026-04-27.md §6.7`.
