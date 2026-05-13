// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Ai.SemanticKernel;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.ParityTests;

/// <summary>
/// Parity between the SK and MAF streaming adapters: given the same pre-scripted
/// text chunks from the underlying stack, <see cref="StatefulAiAgent.StreamAsync(string, CancellationToken)"/>
/// must emit the same deltas in order and settle with the same assistant-turn text
/// in <see cref="IAiAgent.History"/>.
/// </summary>
public sealed class StreamingParityTests
{
    private static readonly string[] ScriptedChunks = { "Hello", ", ", "world", "!" };

    [Fact]
    public async Task SemanticKernel_Adapter_Streams_Deltas_And_Appends_Assistant_Turn()
    {
        var service = new ScriptedStreamingChatCompletionService(ScriptedChunks);
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(service);
        var kernel = kernelBuilder.Build();
        var provider = new SkCompletionProvider(kernel);
        var agent = new StatefulAiAgent(provider);

        var collected = new List<string>();
        await foreach (var delta in agent.StreamAsync("greet"))
        {
            collected.Add(delta);
        }

        collected.Should().Equal(ScriptedChunks);
        string.Concat(collected).Should().Be("Hello, world!");
        agent.History.Should().HaveCount(2);
        agent.History[1].Should().Be(new ChatTurn(AgentChatRole.Assistant, "Hello, world!"));
    }

    [Fact]
    public async Task MicrosoftAgentFramework_Adapter_Streams_Deltas_And_Appends_Assistant_Turn()
    {
        var client = new ScriptedStreamingChatClient(ScriptedChunks);
        var provider = new MafCompletionProvider(client);
        var agent = new StatefulAiAgent(provider);

        var collected = new List<string>();
        await foreach (var delta in agent.StreamAsync("greet"))
        {
            collected.Add(delta);
        }

        collected.Should().Equal(ScriptedChunks);
        string.Concat(collected).Should().Be("Hello, world!");
        agent.History.Should().HaveCount(2);
        agent.History[1].Should().Be(new ChatTurn(AgentChatRole.Assistant, "Hello, world!"));
    }

    [Fact]
    public async Task Both_Adapters_Produce_Equivalent_Streams_For_The_Same_Script()
    {
        // Run the same scripted chunks through both stacks, collect, compare. This is the
        // parity assertion — the neutral CompletionUpdate contract doesn't leak SK- or
        // MAF-specific structure back to the caller.
        var skService = new ScriptedStreamingChatCompletionService(ScriptedChunks);
        var skKernelBuilder = Kernel.CreateBuilder();
        skKernelBuilder.Services.AddSingleton<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(skService);
        var skAgent = new StatefulAiAgent(new SkCompletionProvider(skKernelBuilder.Build()));

        var mafClient = new ScriptedStreamingChatClient(ScriptedChunks);
        var mafAgent = new StatefulAiAgent(new MafCompletionProvider(mafClient));

        var sk = await DrainAsync(skAgent);
        var maf = await DrainAsync(mafAgent);

        sk.Should().Equal(maf);
    }

    private static async Task<List<string>> DrainAsync(StatefulAiAgent agent)
    {
        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("go"))
        {
            deltas.Add(d);
        }
        return deltas;
    }
}
