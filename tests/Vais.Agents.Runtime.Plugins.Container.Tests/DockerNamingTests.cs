// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class DockerNamingTests
{
    [Fact]
    public void ContainerName_ReturnsVaisPluginPrefix()
    {
        DockerNaming.ContainerName("my-plugin").Should().Be("vais-plugin-my-plugin");
    }

    [Fact]
    public void InvokeUrl_NoNetwork_ReturnsLocalhostUrl()
    {
        DockerNaming.InvokeUrl("my-plugin", 8080, null).Should().Be("http://localhost:8080");
    }

    [Fact]
    public void InvokeUrl_EmptyNetwork_ReturnsLocalhostUrl()
    {
        DockerNaming.InvokeUrl("my-plugin", 8080, "").Should().Be("http://localhost:8080");
    }

    [Fact]
    public void InvokeUrl_WithNetwork_ReturnsContainerDnsUrl()
    {
        DockerNaming.InvokeUrl("my-plugin", 8080, "vais-internal")
            .Should().Be("http://vais-plugin-my-plugin:8080");
    }

    [Fact]
    public void InvokeUrl_ContainerDnsHostname_MatchesContainerName()
    {
        var name = "echo-plugin";
        var port = 9090;
        var url = DockerNaming.InvokeUrl(name, port, "vais-internal");
        url.Should().Be($"http://{DockerNaming.ContainerName(name)}:{port}");
    }
}
