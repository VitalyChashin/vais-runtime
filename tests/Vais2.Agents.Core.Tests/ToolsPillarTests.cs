// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class AggregatingToolRegistryTests
{
    [Fact]
    public async Task BuildAsync_Accepts_Null_Inputs()
    {
        var registry = await AggregatingToolRegistry.BuildAsync(null, null);
        registry.Tools.Should().BeEmpty();
        registry.GetByName("anything").Should().BeNull();
    }

    [Fact]
    public async Task Static_Tools_Flow_Through()
    {
        var t1 = new NamedTool("alpha");
        var t2 = new NamedTool("beta");

        var registry = await AggregatingToolRegistry.BuildAsync(new ITool[] { t1, t2 }, null);

        registry.Tools.Should().HaveCount(2);
        registry.GetByName("alpha").Should().BeSameAs(t1);
        registry.GetByName("beta").Should().BeSameAs(t2);
    }

    [Fact]
    public async Task Source_Discovered_Tools_Merge_With_Static_Tools()
    {
        var sTool = new NamedTool("static");
        var dTool1 = new NamedTool("dyn-1");
        var dTool2 = new NamedTool("dyn-2");

        var source = new FixedToolSource(new ITool[] { dTool1, dTool2 });
        var registry = await AggregatingToolRegistry.BuildAsync(
            staticTools: new ITool[] { sTool },
            sources: new IToolSource[] { source });

        registry.Tools.Should().HaveCount(3);
        registry.Tools[0].Should().BeSameAs(sTool);
        registry.GetByName("dyn-1").Should().BeSameAs(dTool1);
        registry.GetByName("dyn-2").Should().BeSameAs(dTool2);
    }

    [Fact]
    public async Task First_Wins_On_Duplicate_Names()
    {
        // Static tool wins over a source-discovered tool of the same name.
        var original = new NamedTool("duplicate");
        var later = new NamedTool("duplicate");
        var source = new FixedToolSource(new ITool[] { later });

        var registry = await AggregatingToolRegistry.BuildAsync(
            new ITool[] { original },
            new IToolSource[] { source });

        registry.GetByName("duplicate").Should().BeSameAs(original);
        // Tools still lists both (visibility), but GetByName resolves to the first.
        registry.Tools.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cancellation_Propagates_Into_Discovery()
    {
        var source = new InfiniteToolSource();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        Func<Task> act = async () => await AggregatingToolRegistry.BuildAsync(null, new IToolSource[] { source }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- helpers ----

    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;
        public string Description => $"tool {name}";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }

    private sealed class FixedToolSource(IEnumerable<ITool> tools) : IToolSource
    {
#pragma warning disable CS1998
        public async IAsyncEnumerable<ITool> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var tool in tools)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return tool;
            }
        }
#pragma warning restore CS1998
    }

    private sealed class InfiniteToolSource : IToolSource
    {
        public async IAsyncEnumerable<ITool> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(5, cancellationToken);
                yield return new NamedTool(Guid.NewGuid().ToString("N"));
            }
        }

        private sealed class NamedTool(string name) : ITool
        {
            public string Name => name;
            public string Description => "inf";
            public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement;
            public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) => Task.FromResult("x");
        }
    }
}

public sealed class ToolFromFuncTests
{
    public sealed record SearchArgs(string Query, int TopK);
    public sealed record SearchResult(string Url, double Score);

    [Fact]
    public void Generates_Schema_From_Input_Type()
    {
        var tool = Tool.FromFunc<SearchArgs, SearchResult>(
            "search", "Search the knowledge base.",
            (_, _) => Task.FromResult(new SearchResult("u", 1.0)));

        tool.Name.Should().Be("search");
        tool.Description.Should().Be("Search the knowledge base.");
        // STJ's JsonSchemaExporter generates an object-shaped schema with nullability
        // unions (e.g., ["string", "null"]). We just confirm the structural shape here;
        // adapters (SK/MAF) do any dialect-specific post-processing if needed.
        var schema = tool.ParametersSchema;
        schema.ValueKind.Should().Be(JsonValueKind.Object);
        schema.GetProperty("properties").ValueKind.Should().Be(JsonValueKind.Object);
        schema.GetProperty("properties").GetProperty("Query").ValueKind.Should().Be(JsonValueKind.Object);
        schema.GetProperty("properties").GetProperty("TopK").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Deserializes_Arguments_Into_Input_Type()
    {
        SearchArgs? captured = null;
        var tool = Tool.FromFunc<SearchArgs, string>(
            "search", "desc",
            (args, _) => { captured = args; return Task.FromResult("done"); });

        var args = JsonDocument.Parse("""{"Query":"cats","TopK":5}""").RootElement;
        var result = await tool.InvokeAsync(args);

        result.Should().Be("done");
        captured.Should().NotBeNull();
        captured!.Query.Should().Be("cats");
        captured.TopK.Should().Be(5);
    }

    [Fact]
    public async Task String_Output_Is_Returned_Verbatim()
    {
        var tool = Tool.FromFunc<SearchArgs, string>(
            "search", "desc",
            (_, _) => Task.FromResult("verbatim string"));

        var result = await tool.InvokeAsync(JsonDocument.Parse("""{"Query":"q","TopK":1}""").RootElement);
        result.Should().Be("verbatim string");
    }

    [Fact]
    public async Task Object_Output_Is_Json_Serialized()
    {
        var tool = Tool.FromFunc<SearchArgs, SearchResult>(
            "search", "desc",
            (_, _) => Task.FromResult(new SearchResult("https://x", 0.9)));

        var result = await tool.InvokeAsync(JsonDocument.Parse("""{"Query":"q","TopK":1}""").RootElement);
        var parsed = JsonDocument.Parse(result).RootElement;
        parsed.GetProperty("Url").GetString().Should().Be("https://x");
        parsed.GetProperty("Score").GetDouble().Should().Be(0.9);
    }

    [Fact]
    public async Task Null_Output_Becomes_Empty_String()
    {
        var tool = Tool.FromFunc<SearchArgs, string?>(
            "search", "desc",
            (_, _) => Task.FromResult<string?>(null));

        var result = await tool.InvokeAsync(JsonDocument.Parse("""{"Query":"q","TopK":1}""").RootElement);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_Json_Arguments_Throw_ArgumentException()
    {
        var tool = Tool.FromFunc<SearchArgs, string>(
            "search", "desc",
            (_, _) => Task.FromResult("ok"));

        // Integer JsonElement can't deserialize into SearchArgs (expects object).
        var bogus = JsonDocument.Parse("42").RootElement;

        Func<Task> act = async () => await tool.InvokeAsync(bogus);
        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "arguments");
    }

    [Fact]
    public async Task No_Arg_Overload_Has_Empty_Object_Schema()
    {
        var called = false;
        var tool = Tool.FromFunc<string>(
            "ping", "desc",
            _ => { called = true; return Task.FromResult("pong"); });

        tool.ParametersSchema.GetProperty("type").GetString().Should().Be("object");
        tool.ParametersSchema.GetProperty("properties").EnumerateObject().Should().BeEmpty();

        var result = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement);
        called.Should().BeTrue();
        result.Should().Be("pong");
    }

    [Fact]
    public async Task Integration_With_StatefulAiAgent_Loop_Invokes_Typed_Handler()
    {
        SearchArgs? observed = null;
        var searchTool = Tool.FromFunc<SearchArgs, string>(
            "search", "Search the KB.",
            (args, _) => { observed = args; return Task.FromResult("""{"hit":"doc-42"}"""); });

        var callArgs = JsonDocument.Parse("""{"Query":"weather","TopK":3}""").RootElement;
        var provider = new ScriptedProvider(
            new CompletionResponse("", ToolCalls: new[] { new ToolCallRequest("search", callArgs, "c1") }),
            new CompletionResponse("Here are the results."));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(searchTool),
        });

        var reply = await agent.AskAsync("find something");

        reply.Should().Be("Here are the results.");
        observed.Should().NotBeNull();
        observed!.Query.Should().Be("weather");
        observed.TopK.Should().Be(3);
    }

    private sealed class ScriptedProvider(params CompletionResponse[] responses) : ICompletionProvider
    {
        private int _index;
        public string ProviderName => "scripted";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            if (_index >= responses.Length) throw new InvalidOperationException("ScriptedProvider exhausted");
            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed class InMemoryToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }
}
