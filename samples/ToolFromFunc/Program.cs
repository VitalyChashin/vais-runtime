// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// ToolFromFunc — shows the typed shortcut Tool.FromFunc<TInput, TOutput> +
// the no-arg Tool.FromFunc<TOutput> + AggregatingToolRegistry.BuildAsync with
// both static tools and an IToolSource. Prints each tool's auto-generated
// JSON schema so the reader sees what the model sees.
// -----------------------------------------------------------------------------

var echo = Tool.FromFunc<EchoInput, string>(
    name: "echo",
    description: "Echo the text back to the caller.",
    handler: (input, ct) => Task.FromResult(input.Text));

var now = Tool.FromFunc<string>(
    name: "current_time",
    description: "Return the current UTC time as ISO-8601.",
    handler: ct => Task.FromResult(DateTimeOffset.UtcNow.ToString("o")));

var addNumbers = Tool.FromFunc<AddInput, int>(
    name: "add",
    description: "Add two integers.",
    handler: (input, ct) => Task.FromResult(input.A + input.B));

var source = new DynamicCatalogSource(new[] { echo, now });

var registry = await AggregatingToolRegistry.BuildAsync(
    staticTools: new ITool[] { addNumbers },
    sources: new IToolSource[] { source });

Console.WriteLine($"Registered tools: {registry.Tools.Count}");
foreach (var t in registry.Tools)
{
    Console.WriteLine($"  - {t.Name}: {t.Description}");
    Console.WriteLine($"      schema: {t.ParametersSchema.GetRawText()}");
}

Console.WriteLine();

// Invoke each tool by name through the registry (as the dispatcher would).
Console.WriteLine("=== direct invocations ===");
var echoResult = await registry.GetByName("echo")!.InvokeAsync(
    System.Text.Json.JsonDocument.Parse("""{"text":"hello"}""").RootElement);
Console.WriteLine($"echo(\"hello\") → {echoResult}");

var addResult = await registry.GetByName("add")!.InvokeAsync(
    System.Text.Json.JsonDocument.Parse("""{"a":3,"b":4}""").RootElement);
Console.WriteLine($"add(3, 4) → {addResult}");

var nowResult = await registry.GetByName("current_time")!.InvokeAsync(
    System.Text.Json.JsonDocument.Parse("{}").RootElement);
Console.WriteLine($"current_time() → {nowResult}");

// ---- Data shapes ----
sealed record EchoInput(string Text);
sealed record AddInput(int A, int B);

// ---- Custom IToolSource ----
sealed class DynamicCatalogSource(IReadOnlyList<ITool> tools) : IToolSource
{
#pragma warning disable CS1998 // body is synchronous on purpose
    public async IAsyncEnumerable<ITool> DiscoverAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var t in tools) yield return t;
    }
#pragma warning restore CS1998
}
