# Wrap third-party tools

**v0.17.** Some NuGet libraries ship pre-built tool sources — they call `AddMyLibraryTools()` and internally register an `INamedToolSourceProvider`. This guide shows how to discover those registrations and override the provider when you need custom configuration (a different API key, base URL, or timeout).

---

## Background: how tool sources are resolved

When the runtime translates an agent manifest, it resolves each tool by name through all registered `INamedToolSourceProvider` instances. The first provider that returns a non-null source for a given name wins. Multiple providers can coexist — Python plugin tools, assembly plugin tools, and any custom providers you register all participate.

```
INamedToolSourceProvider #1  →  GetByName("search")    → non-null? yes → use it
INamedToolSourceProvider #2  →  (never consulted for "search")
```

---

## Step 1: Discover what a library registers

Call `GetServices<INamedToolSourceProvider>()` after building the host:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;

var services = new ServiceCollection();
services.AddThirdPartyLibraryTools(...);  // the library's extension method

await using var provider = services.BuildServiceProvider();
var providers = provider.GetServices<INamedToolSourceProvider>();

foreach (var p in providers)
{
    Console.WriteLine(p.GetType().FullName);
    // Try a few names to see which one the library owns:
    foreach (var name in new[] { "search", "translate", "code_interpreter" })
    {
        var source = p.GetByName(name);
        Console.WriteLine($"  {name}: {(source is null ? "null" : source.GetType().Name)}");
    }
}
```

The output tells you the concrete provider class name and which tool names it handles. You can then decide whether to replace it entirely, wrap it, or leave it as-is.

---

## Step 2: Override with your own provider

Register your implementation **before** the library's extension method. Because the translator iterates providers in registration order and uses the first non-null result, registering first guarantees your provider wins for any name it handles.

```csharp
services.AddSingleton<INamedToolSourceProvider>(new MySearchToolProvider(
    apiKey: config["Search:ApiKey"],
    baseUrl: new Uri(config["Search:BaseUrl"]),
    timeoutSeconds: 30));

// Library's registration comes after — its provider is still in the container,
// but the translator never reaches it for names your provider owns.
services.AddThirdPartyLibraryTools();
```

`MySearchToolProvider` implements `INamedToolSourceProvider`:

```csharp
using Vais.Agents;

sealed class MySearchToolProvider(string apiKey, Uri baseUrl, int timeoutSeconds) : INamedToolSourceProvider
{
    public IToolSource? GetByName(string name) =>
        string.Equals(name, "search", StringComparison.Ordinal)
            ? new SearchToolSource(apiKey, baseUrl, TimeSpan.FromSeconds(timeoutSeconds))
            : null;
}
```

Return `null` for any name you don't own — the translator continues to the next provider.

---

## Step 3: Wrap (rather than replace)

If you want to add a caching layer or metrics around the library's existing source, resolve it from DI and delegate:

```csharp
sealed class CachingSearchProvider(INamedToolSourceProvider inner) : INamedToolSourceProvider
{
    public IToolSource? GetByName(string name)
    {
        var source = inner.GetByName(name);
        return source is null ? null : new CachedToolSource(source);
    }
}

// Wire the original provider as a named dependency, then wrap it.
services.AddSingleton<ThirdPartySearchProvider>();  // concrete type
services.AddSingleton<INamedToolSourceProvider>(sp =>
    new CachingSearchProvider(sp.GetRequiredService<ThirdPartySearchProvider>()));

services.AddThirdPartyLibraryTools();  // registers its own provider last (unused for "search")
```

---

## Registration order matters

| Registration order | Behaviour |
|---|---|
| Your provider first | Your provider wins for any name it handles |
| Library first, yours second | Library wins — yours never consulted |
| Only yours | Library tools unavailable by name; error at translate time |

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Tool source not found for 'search'" at agent invoke time | No registered provider handles that name | Confirm the library's extension method ran, or register a custom provider |
| Library's provider still being used despite override | Provider registered after the library | Move your `AddSingleton<INamedToolSourceProvider>` call before the library's extension method |
| Both providers return non-null for the same name | Name collision | Narrow your provider's `GetByName` guard to return null for names you don't intend to own |

## See also

- [Wire a custom tool](wire-a-custom-tool.md)
- [Package a Python plugin](package-a-python-plugin.md)
- [Expose MCP tools to an agent](expose-mcp-tools-to-an-agent.md)
