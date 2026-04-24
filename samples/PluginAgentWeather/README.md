# PluginAgentWeather — code-authored agent as a v0.18 plugin

Ships an `IAiAgent` implementation as a loadable plugin for the Vais.Agents runtime (v0.18 Pillar C). The runtime container image scans `/var/lib/vais/plugins` at startup; each subfolder is a plugin, loaded into an isolated `AssemblyLoadContext`.

## What this shows

- `[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent")]` declares the assembly as a plugin and lists the handler `TypeName`s it exports.
- A sealed `public class WeatherAgent : IAiAgent` — just the Abstractions surface, no framework glue. Constructor can take any service registered in the host DI container (`IHttpClientFactory`, `ICompletionProvider`, `ISecretResolver`, …) because the default handler factory uses `ActivatorUtilities.CreateInstance`.
- `CopyLocalLockFileAssemblies=true` + `SelfContained=false` so `dotnet publish` emits every transitive dependency alongside the primary DLL. The loader's per-plugin `AssemblyDependencyResolver` picks them up without mixing runtime trees.

## Build + publish

```bash
cd samples/PluginAgentWeather/MyApp.WeatherAgent
dotnet publish -c Release -o ../publish
```

The `publish/` folder now contains `MyApp.WeatherAgent.dll` + its transitive deps (minus whatever is in the runtime host's shared-types carve-out — `Vais.Agents.Abstractions`, `Vais.Agents.Core`, etc.).

## Layer onto the runtime image

```bash
cd samples/PluginAgentWeather
docker build -f Dockerfile.overlay -t my-weather-runtime:0.18.0-preview .
```

Or bind-mount at run time without a custom image:

```bash
docker run --rm -p 8080:8080 \
  -v "$(pwd)/publish:/var/lib/vais/plugins/weather-agent:ro" \
  ghcr.io/vais-agents/runtime:0.18.0-preview
```

Check the startup log — look for `plugins=1 handlers=[MyApp.WeatherAgent]`.

## Apply the manifest + invoke

```yaml
# weather.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: weather
spec:
  handler:
    typeName: MyApp.WeatherAgent
  protocols:
    - kind: Http
  tools: []
```

```bash
vais apply -f weather.yaml
vais invoke weather -m "hello"
# → "Sunny!"
```

No `ModelSpec` is needed because the plugin carries its own logic. If you set one anyway, the translator emits a `urn:vais-agents:handler-and-declarative-fields-both-set` warning on the apply response; the plugin wins and declarative fields are ignored.

## Hot-reload without restarting the host (v0.22)

Set `VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap` and the runtime starts a background filesystem watcher on the plugins directory. When you copy a new build of `MyApp.WeatherAgent.dll` into the plugin folder the runtime:

1. Loads the new DLL into a fresh collectible `AssemblyLoadContext`.
2. Runs any registered `IPluginReloadHook` implementations (drain in-flight requests, etc.).
3. Atomically swaps the handler registry entry — all subsequent `TranslateAsync` calls resolve against the new code.
4. Calls `Unload()` on the old context so it can be collected by the GC.

No pod restart, no manifest re-apply.

### Try it

```bash
# Start with DrainAndSwap enabled
docker run --rm -p 8080:8080 \
  -e VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap \
  -v "$(pwd)/publish:/var/lib/vais/plugins/weather-agent" \
  ghcr.io/vais-agents/runtime:0.22.0-preview

# While the container is running, publish a new build and copy it in
dotnet publish -c Release -o ./publish-v2
cp publish-v2/MyApp.WeatherAgent.dll publish/MyApp.WeatherAgent.dll

# The runtime log shows: [PluginReloader] swap complete: MyApp.WeatherAgent v1→v2
```

The policy defaults to `Disabled` (identical to v0.18 behaviour) so existing deployments are unaffected unless you opt in.

## Disabling the plugin loader

`VAIS_PLUGINS_DIRECTORY=""` turns the loader off. The translator then treats every manifest as a v0.17 declarative agent.

## See also

- [docs/concepts/runtime-plugins.md](../../docs/concepts/runtime-plugins.md) — loader internals, isolation model, ABI-matching rules, URN catalogue.
- [docs/guides/package-an-agent-as-a-plugin.md](../../docs/guides/package-an-agent-as-a-plugin.md) — step-by-step walk-through parallel to `author-an-agent-in-yaml.md`.
