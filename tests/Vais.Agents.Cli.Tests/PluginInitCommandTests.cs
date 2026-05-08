// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class PluginInitCommandTests
{
    [Fact]
    public void BuildPythonScaffold_ContainsExpectedFields()
    {
        var (yaml, dockerfile) = PluginInitCommand.BuildPythonScaffold("research-planner");

        yaml.Should().Contain("name: research-planner");
        yaml.Should().Contain("runtime: python");
        yaml.Should().Contain("entrypoint: src/server.py");
        yaml.Should().Contain("apiVersion: vais.agents/v1");
        dockerfile.Should().BeNull();
    }

    [Fact]
    public void BuildDotnetScaffold_ContainsContainerRuntime()
    {
        var (yaml, dockerfile) = PluginInitCommand.BuildDotnetScaffold("my-plugin");

        yaml.Should().Contain("name: my-plugin");
        yaml.Should().Contain("runtime: container");
        yaml.Should().Contain("image: my-registry/my-plugin:latest");
        yaml.Should().Contain("apiVersion: vais.agents/v1");
    }

    [Fact]
    public void BuildDotnetScaffold_GeneratesDockerfile()
    {
        var (_, dockerfile) = PluginInitCommand.BuildDotnetScaffold("my-plugin");

        dockerfile.Should().NotBeNull();
        dockerfile.Should().Contain("dotnet/sdk:9.0");
        dockerfile.Should().Contain("dotnet/aspnet:9.0");
        dockerfile.Should().Contain("my-plugin.dll");
        dockerfile.Should().Contain("EXPOSE 8080");
    }
}
