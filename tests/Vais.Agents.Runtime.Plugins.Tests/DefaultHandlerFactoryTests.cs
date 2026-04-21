// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

public class DefaultHandlerFactoryTests
{
    [Fact]
    public async Task Create_Returns_IAiAgent_Instance_Via_ActivatorUtilities()
    {
        var factory = DefaultHandlerFactory.Create(typeof(TrivialAgent), "MyApp.TrivialAgent");

        var sp = new ServiceCollection().BuildServiceProvider();
        var manifest = BuildManifest();

        var agent = await factory.CreateAsync(manifest, sp);

        agent.Should().BeOfType<TrivialAgent>();
        factory.HandlerTypeName.Should().Be("MyApp.TrivialAgent");
    }

    [Fact]
    public async Task Create_Resolves_Constructor_Dependencies_From_DI()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestDependency("hello-from-DI"));
        using var sp = services.BuildServiceProvider();

        var factory = DefaultHandlerFactory.Create(typeof(DiAwareAgent), "MyApp.DiAware");
        var agent = (DiAwareAgent)await factory.CreateAsync(BuildManifest(), sp);

        agent.Dependency.Marker.Should().Be("hello-from-DI");
    }

    [Fact]
    public void Create_Rejects_NonIAiAgent_Types()
    {
        Action act = () => DefaultHandlerFactory.Create(typeof(string), "MyApp.NotAnAgent");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not implement IAiAgent*");
    }

    private static AgentManifest BuildManifest() => new(
        Id: "test",
        Version: "1.0",
        Handler: new AgentHandlerRef("MyApp.TrivialAgent"),
        Protocols: Array.Empty<ProtocolBinding>(),
        Tools: Array.Empty<ToolRef>());

    public sealed class TrivialAgent : IAiAgent
    {
        public string? SystemPrompt { get; set; }
        public IAgentSession Session => throw new NotImplementedException();
        public IReadOnlyList<ChatTurn> History => Array.Empty<ChatTurn>();
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default) => Task.FromResult("trivial");
        public void Reset() { }
    }

    public sealed class DiAwareAgent(TestDependency dependency) : IAiAgent
    {
        public TestDependency Dependency { get; } = dependency;
        public string? SystemPrompt { get; set; }
        public IAgentSession Session => throw new NotImplementedException();
        public IReadOnlyList<ChatTurn> History => Array.Empty<ChatTurn>();
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default) => Task.FromResult("di");
        public void Reset() { }
    }

    public sealed class TestDependency(string marker)
    {
        public string Marker { get; } = marker;
    }
}
