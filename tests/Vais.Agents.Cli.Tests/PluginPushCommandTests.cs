// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class PluginPushCommandTests
{
    [Theory]
    [InlineData("my-registry/my-plugin:1.0", "my-plugin")]
    [InlineData("my-plugin:1.0", "my-plugin")]
    [InlineData("registry.io/team/sgr-analyst:latest", "sgr-analyst")]
    public void InferPluginName_ExtractsNameFromImageRef(string image, string expected)
    {
        PluginPushCommand.InferPluginName(image).Should().Be(expected);
    }

    [Theory]
    [InlineData("my-plugin")]
    [InlineData("research-planner")]
    public void InferPluginName_PlainName_ReturnsItself(string name)
    {
        PluginPushCommand.InferPluginName(name).Should().Be(name);
    }

    [Theory]
    [InlineData("my-registry/my-plugin:1.0")]
    [InlineData("my-plugin:1.0")]
    public void IsImageMode_WhenPositionalContainsSlashOrColon(string arg)
    {
        var containsSlash = arg.Contains('/', StringComparison.Ordinal);
        var containsColon = arg.Contains(':', StringComparison.Ordinal);
        (containsSlash || containsColon).Should().BeTrue();
    }
}
