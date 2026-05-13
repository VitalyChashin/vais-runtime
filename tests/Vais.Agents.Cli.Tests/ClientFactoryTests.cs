// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class ClientFactoryTests
{
    [Fact]
    public void Create_NoCurrentContext_Throws()
    {
        var config = new VaisCliConfig();

        FluentActions.Invoking(() => ClientFactory.Create(config))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*No context selected*");
    }

    [Fact]
    public void Create_ContextMissingInConfig_Throws()
    {
        var config = new VaisCliConfig { CurrentContext = "absent" };

        FluentActions.Invoking(() => ClientFactory.Create(config))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Context 'absent' not found*");
    }

    [Fact]
    public void Create_ClusterMissing_Throws()
    {
        var config = new VaisCliConfig
        {
            CurrentContext = "default",
            Contexts = { new VaisContext { Name = "default", Cluster = "absent", User = "u" } },
            Users = { new VaisUser { Name = "u" } },
        };

        FluentActions.Invoking(() => ClientFactory.Create(config))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Cluster 'absent'*missing*");
    }

    [Fact]
    public void Create_WithValidContext_ReturnsClient()
    {
        var config = new VaisCliConfig
        {
            CurrentContext = "default",
            Clusters = { new VaisCluster { Name = "local", Server = "http://localhost:5080" } },
            Users = { new VaisUser { Name = "dev", Token = "tok" } },
            Contexts = { new VaisContext { Name = "default", Cluster = "local", User = "dev" } },
        };

        var client = ClientFactory.Create(config);
        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_ContextOverride_WinsOverCurrentContext()
    {
        var config = new VaisCliConfig
        {
            CurrentContext = "default",
            Clusters =
            {
                new VaisCluster { Name = "local", Server = "http://localhost:5080" },
                new VaisCluster { Name = "other", Server = "http://other:9000" },
            },
            Users = { new VaisUser { Name = "u" } },
            Contexts =
            {
                new VaisContext { Name = "default", Cluster = "local", User = "u" },
                new VaisContext { Name = "alt", Cluster = "other", User = "u" },
            },
        };

        // Must not throw when overriding — picks the alt context with its cluster.
        var client = ClientFactory.Create(config, contextOverride: "alt");
        client.Should().NotBeNull();
    }
}
