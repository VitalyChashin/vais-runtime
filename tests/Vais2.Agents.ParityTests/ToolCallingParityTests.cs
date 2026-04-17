// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Vais2.Agents.Ai.MicrosoftAgentFramework;
using Vais2.Agents.Ai.SemanticKernel;
using Vais2.Agents.Core;
using Xunit;

namespace Vais2.Agents.ParityTests;

/// <summary>
/// Parity of tool-call wiring across SK and MAF adapters. The tests verify that a
/// single neutral <see cref="ITool"/> is bound correctly on both sides and actually
/// invoked when the downstream stack selects it.
/// </summary>
/// <remarks>
/// <para>
/// The two stacks handle auto-invocation at different layers — MAF's
/// <c>FunctionInvokingChatClient</c> lives in the generic <c>IChatClient</c> pipeline
/// and therefore works with any <c>IChatClient</c> implementation, while SK's
/// auto-invocation is a responsibility of the concrete connector
/// (e.g. <c>OpenAIChatCompletionService</c>) — a raw <c>IChatCompletionService</c>
/// test double does not auto-invoke.
/// </para>
/// <para>
/// That asymmetry is the reason the MAF scenario exercises the full
/// <c>StatefulAiAgent.AskAsync</c> → tool-call → final-reply loop end-to-end, while
/// the SK scenario inspects the produced <see cref="KernelPlugin"/> directly and
/// invokes one of its functions to prove the bridge is wired correctly.
/// </para>
/// </remarks>
public sealed class ToolCallingParityTests
{
    [Fact]
    public async Task MicrosoftAgentFramework_Adapter_Invokes_Tool_Via_FunctionInvokingChatClient()
    {
        var tool = new RecordingTool();

        // Two-step script: (1) tool call, (2) final assistant text.
        var callMsg = new MeaiChatMessage(MeaiChatRole.Assistant, contents:
        [
            new MeaiFunctionCallContent(
                callId: "call-1",
                name: "get_weather",
                arguments: new Dictionary<string, object?> { ["city"] = "Paris" }),
        ]);
        var finalMsg = new MeaiChatMessage(MeaiChatRole.Assistant, "It's 72 degrees in Paris.");

        var client = new ScriptedChatClient(new[]
        {
            new ChatResponse(callMsg),
            new ChatResponse(finalMsg),
        });

        var provider = new MafCompletionProvider(client);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
        });

        var reply = await agent.AskAsync("What's the weather in Paris?");

        reply.Should().Be("It's 72 degrees in Paris.");
        tool.Invocations.Should().ContainSingle();
        tool.Invocations[0].GetProperty("city").GetString().Should().Be("Paris");
        client.Invocations.Should().HaveCount(2);
    }

    [Fact]
    public void SemanticKernel_Binding_Produces_KernelPlugin_With_Matching_Schema()
    {
        var tool = new RecordingTool();

        var plugin = SkToolBinder.BuildPlugin(new[] { (ITool)tool });

        plugin.Name.Should().Be("Tools");
        plugin.Should().ContainSingle();
        var fn = plugin.First();
        fn.Name.Should().Be("get_weather");
        fn.Description.Should().Be("Return the current weather for a city.");
    }

    [Fact]
    public async Task SemanticKernel_KernelFunction_Invocation_Calls_Through_To_ITool()
    {
        var tool = new RecordingTool(reply: """{"temperature":72,"unit":"F"}""");
        var plugin = SkToolBinder.BuildPlugin(new[] { (ITool)tool });

        var kernel = new Kernel();
        kernel.Plugins.Add(plugin);

        var result = await kernel.InvokeAsync("Tools", "get_weather", new KernelArguments { ["city"] = "Paris" });

        tool.Invocations.Should().ContainSingle();
        tool.Invocations[0].GetProperty("city").GetString().Should().Be("Paris");
        result.GetValue<string>().Should().Be("""{"temperature":72,"unit":"F"}""");
    }

    [Fact]
    public async Task MicrosoftAgentFramework_AIFunction_Invocation_Calls_Through_To_ITool()
    {
        var tool = new RecordingTool(reply: """{"temperature":72,"unit":"F"}""");
        var tools = MafToolBinder.BuildTools(new[] { (ITool)tool });

        tools.Should().ContainSingle();
        var aiFunction = tools[0].Should().BeAssignableTo<AIFunction>().Which;
        aiFunction.Name.Should().Be("get_weather");
        aiFunction.Description.Should().Be("Return the current weather for a city.");

        var args = new AIFunctionArguments { ["city"] = "Paris" };
        var result = await aiFunction.InvokeAsync(args);

        tool.Invocations.Should().ContainSingle();
        tool.Invocations[0].GetProperty("city").GetString().Should().Be("Paris");
        result.Should().Be("""{"temperature":72,"unit":"F"}""");
    }

    [Fact]
    public void Both_Adapters_Expose_The_Same_Tool_Identity()
    {
        // The point of a neutral ITool is that Name, Description, and ParametersSchema
        // round-trip identically through either adapter's binding layer. A drift here
        // means one side is munging the contract.
        var tool = new RecordingTool();

        var skPlugin = SkToolBinder.BuildPlugin(new[] { (ITool)tool });
        var mafTools = MafToolBinder.BuildTools(new[] { (ITool)tool });

        var skFn = skPlugin.Single();
        var mafFn = mafTools.Single();

        skFn.Name.Should().Be(mafFn.Name);
        skFn.Description.Should().Be(mafFn.Description);
    }

    /// <summary>Trivial in-memory <see cref="IToolRegistry"/> used only by these tests.</summary>
    private sealed class InMemoryToolRegistry : IToolRegistry
    {
        public InMemoryToolRegistry(params ITool[] tools)
        {
            Tools = tools;
        }

        public IReadOnlyList<ITool> Tools { get; }

        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }
}
