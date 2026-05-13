// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Ai.SemanticKernel;
using Vais.Agents.Core;
using Xunit;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;

namespace Vais.Agents.ParityTests;

/// <summary>
/// Parity coverage for the v0.4.1 tool-using streaming path: both adapters must
/// accumulate model-requested tool calls across streamed updates and surface
/// them to <see cref="StatefulAiAgent.StreamAsync(string, CancellationToken)"/>'s outer loop for dispatch.
/// </summary>
/// <remarks>
/// <para>
/// The MAF scenario runs end-to-end through
/// <see cref="StatefulAiAgent.StreamAsync(string, CancellationToken)"/> — MAF's streaming pipeline handles
/// scripted <c>FunctionCallContent</c> cleanly even without the built-in
/// <c>FunctionInvokingChatClient</c> wrapping (we disabled it via
/// <c>UseProvidedChatClientAsIs</c>). The SK scenario is narrower: it asserts
/// <see cref="SkCompletionProvider.StreamAsync"/> emits a terminal
/// <see cref="CompletionUpdate"/> populated with <see cref="CompletionUpdate.ToolCalls"/>
/// when the scripted <c>IChatCompletionService</c> yields a
/// <c>StreamingFunctionCallUpdateContent</c>. That's the parity bridge —
/// accumulation itself is SK's <c>FunctionCallContentBuilder</c>, not ours.
/// </para>
/// </remarks>
public sealed class StreamingToolCallingParityTests
{
    [Fact]
    public async Task MicrosoftAgentFramework_Streaming_Dispatches_Tool_Call_And_Emits_Next_Turn_Text()
    {
        var tool = new RecordingTool(reply: """{"temperature":72,"unit":"F"}""");

        // Two streamed turns scripted:
        //   Turn 1: one update carrying a FunctionCallContent (model requests the tool).
        //   Turn 2: two text updates ("It's " + "72F in Paris.").
        var turn1 = new List<ChatResponseUpdate>
        {
            new(MeaiChatRole.Assistant, new List<AIContent>
            {
                new MeaiFunctionCallContent(
                    callId: "call-1",
                    name: "get_weather",
                    arguments: new Dictionary<string, object?> { ["city"] = "Paris" }),
            }),
        };
        var turn2 = new List<ChatResponseUpdate>
        {
            new(MeaiChatRole.Assistant, "It's "),
            new(MeaiChatRole.Assistant, "72F in Paris."),
        };
        var client = new ScriptedStreamingChatClient(new[] { turn1, turn2 });

        var provider = new MafCompletionProvider(client);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
        });

        var collected = new List<string>();
        await foreach (var delta in agent.StreamAsync("What's the weather in Paris?"))
        {
            collected.Add(delta);
        }

        string.Concat(collected).Should().Be("It's 72F in Paris.");
        tool.Invocations.Should().ContainSingle();
        tool.Invocations[0].GetProperty("city").GetString().Should().Be("Paris");

        // Session stays clean — user + final assistant only.
        agent.History.Should().HaveCount(2);
        agent.History[0].Role.Should().Be(AgentChatRole.User);
        agent.History[1].Role.Should().Be(AgentChatRole.Assistant);
        agent.History[1].Text.Should().Be("It's 72F in Paris.");

        // Two streamed turns = two scripted scripts consumed.
        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task SemanticKernel_Streaming_Emits_Terminal_Update_With_Accumulated_ToolCalls()
    {
        // Script an SK stream that emits one text delta plus a FunctionCallUpdate. The SK
        // connector's FunctionCallContentBuilder accumulates it on our behalf; the adapter
        // must emit a terminal CompletionUpdate with the rebuilt ToolCallRequest.
        var service = new ScriptedStreamingChatCompletionService(new ScriptedStreamingChatCompletionService.Chunk[]
        {
            new(Text: "Looking..."),
            new(FunctionCallUpdate: new StreamingFunctionCallUpdate(
                CallId: "call-9",
                FunctionName: "get_weather",
                ArgumentsFragment: """{"city":"Paris"}""")),
        });
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<IChatCompletionService>(service);
        var kernel = kernelBuilder.Build();
        var provider = new SkCompletionProvider(kernel);

        var tool = new RecordingTool();
        var request = new CompletionRequest(
            new List<ChatTurn> { new(AgentChatRole.User, "What's the weather in Paris?") },
            SystemPrompt: null,
            Tools: new[] { (ITool)tool });

        var updates = new List<CompletionUpdate>();
        await foreach (var update in provider.StreamAsync(request))
        {
            updates.Add(update);
        }

        // Text deltas emitted in order (the function-call chunk carries no text,
        // so it passes through as an empty delta) plus a terminal tool-call update.
        string.Concat(updates.Select(u => u.TextDelta)).Should().Be("Looking...");

        // Non-text updates before the terminal carry no ToolCalls — only the
        // terminal update emitted after the stream drains has them.
        updates.Take(updates.Count - 1).Should().OnlyContain(u => u.ToolCalls == null);

        var terminal = updates[^1];
        terminal.TextDelta.Should().BeEmpty();
        terminal.ToolCalls.Should().NotBeNull().And.HaveCount(1);
        terminal.ToolCalls![0].ToolName.Should().Be("get_weather");
        terminal.ToolCalls[0].CallId.Should().Be("call-9");
        terminal.ToolCalls[0].Arguments.GetProperty("city").GetString().Should().Be("Paris");
    }

    /// <summary>Trivial in-memory <see cref="IToolRegistry"/> used only by these tests.</summary>
    private sealed class InMemoryToolRegistry : IToolRegistry
    {
        public InMemoryToolRegistry(params ITool[] tools) { Tools = tools; }
        public IReadOnlyList<ITool> Tools { get; }
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }
}
