// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Validates the preprocessor extensibility contract: custom preprocessors can be added
/// without modifying <c>ContainerAgentShim</c>.
/// </summary>
public sealed class PreprocessorChainExtensionPointTests
{
    [Fact]
    public void AddContainerPlugins_RegistersExactlyTwoBuiltIns()
    {
        var services = new ServiceCollection();
        services.AddContainerPlugins();

        var preprocessors = services.BuildServiceProvider()
            .GetServices<IAgentPreprocessor>()
            .ToArray();

        preprocessors.Should().HaveCount(2);
    }

    [Fact]
    public void AddAgentPreprocessor_CustomOrder100_RunsAfterBuiltIns()
    {
        var services = new ServiceCollection();
        services.AddContainerPlugins();
        services.AddAgentPreprocessor<StubPreprocessor100>();

        var sorted = services.BuildServiceProvider()
            .GetServices<IAgentPreprocessor>()
            .OrderBy(p => p.Order)
            .ToArray();

        sorted.Should().HaveCount(3);
        sorted[0].Order.Should().Be(0);   // HistoryAssembler
        sorted[1].Order.Should().Be(10);  // SystemPromptInjector
        sorted[2].Order.Should().Be(100); // custom
    }

    [Fact]
    public void AddAgentPreprocessor_MultipleCustom_OrderedCorrectly()
    {
        var services = new ServiceCollection();
        services.AddContainerPlugins();
        services.AddAgentPreprocessor<StubPreprocessor200>();
        services.AddAgentPreprocessor<StubPreprocessor100>();

        var sorted = services.BuildServiceProvider()
            .GetServices<IAgentPreprocessor>()
            .OrderBy(p => p.Order)
            .ToArray();

        sorted.Should().HaveCount(4);
        sorted[2].Order.Should().Be(100);
        sorted[3].Order.Should().Be(200);
    }

    private sealed class StubPreprocessor100 : IAgentPreprocessor
    {
        public int Order => 100;

        public ValueTask<IReadOnlyList<ChatTurn>> ProcessAsync(
            AgentPreprocessorContext context,
            IReadOnlyList<ChatTurn> messages,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(messages);
    }

    private sealed class StubPreprocessor200 : IAgentPreprocessor
    {
        public int Order => 200;

        public ValueTask<IReadOnlyList<ChatTurn>> ProcessAsync(
            AgentPreprocessorContext context,
            IReadOnlyList<ChatTurn> messages,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(messages);
    }
}
