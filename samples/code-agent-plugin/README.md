# code-agent-plugin

Ship a code-authored `IAiAgent` implementation as a loadable plugin for the Vais.Agents runtime (v0.18 Pillar C). This sample extends the `PluginAgentWeather` pattern with dependency injection: the `TranslateAgent` receives `IHttpClientFactory` from the runtime host DI container and calls the OpenAI chat completions API directly.

**Concepts:** [runtime plugins](../../docs/concepts/runtime-plugins.md), [package an agent as a plugin](../../docs/guides/package-an-agent-as-a-plugin.md).
**Needs API key:** yes — `OPENAI_API_KEY` (gracefully degraded when absent).
**Code:** ~80 lines — one `IAiAgent` implementation.

---

## What this shows

- `[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.TranslateAgent")]` declares the handler TypeName the loader exports.
- Constructor injection of `IHttpClientFactory` — any service registered in the runtime host's DI container is available.
- Reading target language from the manifest's `spec.systemPrompt.inline` field so operators can change behaviour without redeploying the DLL.
- Graceful degradation when `OPENAI_API_KEY` is absent — useful for smoke-testing the plugin loading mechanism itself.
- `CopyLocalLockFileAssemblies=true` + `SelfContained=false` so `dotnet publish` emits every transitive dependency alongside the primary DLL.

---

## Build + publish

```bash
cd samples/code-agent-plugin/MyApp.TranslateAgent
dotnet publish -c Release -o ../publish
```

The `publish/` folder now contains `MyApp.TranslateAgent.dll` plus transitive deps, minus the shared-types carve-out (`Vais.Agents.Abstractions`, `Vais.Agents.Core`, etc.).

---

## Layer onto the runtime image

```bash
cd samples/code-agent-plugin
docker build -f Dockerfile.overlay -t my-translate-runtime:0.20.0-preview .
```

Or bind-mount at run time without a custom image:

```bash
docker run --rm -p 8080:8080 \
  -e OPENAI_API_KEY=sk-... \
  -v "$(pwd)/publish:/var/lib/vais/plugins/translate-agent:ro" \
  ghcr.io/vais-agents/runtime:0.20.0-preview
```

Check the startup log — look for `plugins=1 handlers=[MyApp.TranslateAgent]`.

---

## Apply the manifest + invoke

```bash
vais apply -f samples/code-agent-plugin/translate.yaml
# applied Agent translate@1.0

vais invoke translate --text "Good morning, how are you?"
# Buenos días, ¿cómo estás?
```

Change the target language without rebuilding — edit `translate.yaml`:

```yaml
  systemPrompt:
    inline: "targetLanguage: French"
```

```bash
vais apply -f samples/code-agent-plugin/translate.yaml
vais invoke translate --text "Good morning, how are you?"
# Bonjour, comment allez-vous ?
```

---

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `OPENAI_API_KEY` | — | Required for live translation. Agent returns a placeholder if absent. |

---

## See also

- [docs/concepts/runtime-plugins.md](../../docs/concepts/runtime-plugins.md)
- [docs/guides/package-an-agent-as-a-plugin.md](../../docs/guides/package-an-agent-as-a-plugin.md)
- [samples/PluginAgentWeather](../PluginAgentWeather) — hermetic version with no outbound HTTP
