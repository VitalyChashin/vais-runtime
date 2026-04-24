// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

public class PluginHandlerRegistryTests
{
    [Fact]
    public void Empty_Registry_Returns_Empty_Collections()
    {
        var registry = new PluginHandlerRegistry();

        registry.HandlerTypeNames.Should().BeEmpty();
        registry.Plugins.Should().BeEmpty();
        registry.TryGet("anything", out var factory).Should().BeFalse();
        factory.Should().BeNull();
    }

    [Fact]
    public void Register_Adds_Factory_Findable_By_TryGet()
    {
        var registry = new PluginHandlerRegistry();
        var factory = new FakeFactory("MyApp.Foo");

        registry.Register(factory, ownerPluginName: "foo-plugin");

        registry.TryGet("MyApp.Foo", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(factory);
        registry.HandlerTypeNames.Should().ContainSingle().Which.Should().Be("MyApp.Foo");
    }

    [Fact]
    public void Register_Twice_Same_TypeName_Throws_Collision()
    {
        var registry = new PluginHandlerRegistry();
        registry.Register(new FakeFactory("MyApp.Foo"), "plugin-a");

        Action act = () => registry.Register(new FakeFactory("MyApp.Foo"), "plugin-b");

        act.Should().Throw<PluginLoadException>()
            .Which.Urn.Should().Be(PluginUrns.PluginHandlerCollision);
    }

    [Fact]
    public void TryGet_Is_CaseSensitive_Ordinal()
    {
        var registry = new PluginHandlerRegistry();
        registry.Register(new FakeFactory("MyApp.Foo"), "plugin-a");

        registry.TryGet("myapp.foo", out _).Should().BeFalse(
            because: "handler names match ordinal-case, matching the AgentHandlerRef.TypeName contract.");
    }

    [Fact]
    public void RecordPlugin_Appends_Descriptor()
    {
        var registry = new PluginHandlerRegistry();
        var descriptor = new PluginDescriptor(
            Name: "weather",
            AssemblyPath: "/plugins/weather/weather.dll",
            TargetApiVersion: "0.18",
            Handlers: new[] { "MyApp.Weather" },
            LoadedViaAttribute: true,
            LoadContext: System.Runtime.Loader.AssemblyLoadContext.Default);

        registry.RecordPlugin(descriptor);

        registry.Plugins.Should().ContainSingle().Which.Should().Be(descriptor);
    }

    [Fact]
    public async Task SwapAsync_Replaces_Factories_And_Returns_Old_Descriptor()
    {
        var registry = new PluginHandlerRegistry();
        var oldFactory = new FakeFactory("MyApp.Foo");
        var oldDescriptor = new PluginDescriptor(
            Name: "foo-plugin",
            AssemblyPath: "/plugins/foo/foo.dll",
            TargetApiVersion: "0.22",
            Handlers: new[] { "MyApp.Foo" },
            LoadedViaAttribute: true,
            LoadContext: System.Runtime.Loader.AssemblyLoadContext.Default);

        registry.Register(oldFactory, "foo-plugin");
        registry.RecordPlugin(oldDescriptor);

        var newFactory = new FakeFactory("MyApp.Foo");
        var newDescriptor = new PluginDescriptor(
            Name: "foo-plugin",
            AssemblyPath: "/plugins/foo/foo.dll",
            TargetApiVersion: "0.22",
            Handlers: new[] { "MyApp.Foo" },
            LoadedViaAttribute: true,
            LoadContext: System.Runtime.Loader.AssemblyLoadContext.Default);

        var returned = await registry.SwapAsync(
            "foo-plugin",
            newDescriptor,
            new Dictionary<string, IAgentHandlerFactory> { ["MyApp.Foo"] = newFactory });

        returned.Should().BeSameAs(oldDescriptor);
        registry.TryGet("MyApp.Foo", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(newFactory, "registry should now point to the new factory");
        registry.Plugins.Should().ContainSingle().Which.Should().Be(newDescriptor);
    }

    [Fact]
    public async Task SwapAsync_FirstLoad_Returns_Null_OldDescriptor()
    {
        var registry = new PluginHandlerRegistry();
        var newFactory = new FakeFactory("MyApp.Bar");
        var newDescriptor = new PluginDescriptor(
            Name: "bar-plugin",
            AssemblyPath: "/plugins/bar/bar.dll",
            TargetApiVersion: "0.22",
            Handlers: new[] { "MyApp.Bar" },
            LoadedViaAttribute: true,
            LoadContext: System.Runtime.Loader.AssemblyLoadContext.Default);

        var returned = await registry.SwapAsync(
            "bar-plugin",
            newDescriptor,
            new Dictionary<string, IAgentHandlerFactory> { ["MyApp.Bar"] = newFactory });

        returned.Should().BeNull("no prior descriptor existed for bar-plugin");
        registry.TryGet("MyApp.Bar", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(newFactory);
    }

    [Fact]
    public void GetAllFactories_Returns_All_Registered_Factories()
    {
        var registry = new PluginHandlerRegistry();
        var fooFactory = new FakeFactory("MyApp.Foo");
        var barFactory = new FakeFactory("MyApp.Bar");
        registry.Register(fooFactory, "foo-plugin");
        registry.Register(barFactory, "bar-plugin");

        var all = registry.GetAllFactories();

        all.Should().HaveCount(2);
        all["MyApp.Foo"].Should().BeSameAs(fooFactory);
        all["MyApp.Bar"].Should().BeSameAs(barFactory);
    }

    private sealed class FakeFactory(string handlerTypeName) : IAgentHandlerFactory
    {
        public string HandlerTypeName { get; } = handlerTypeName;

        public ValueTask<IAiAgent> CreateAsync(
            AgentManifest manifest,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Fake factory; tests don't invoke CreateAsync.");
    }
}
