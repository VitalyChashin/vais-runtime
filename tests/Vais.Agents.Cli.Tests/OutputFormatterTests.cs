// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Spectre.Console.Testing;
using Vais.Agents.Cli;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class OutputFormatterTests
{
    [Fact]
    public void Parse_Null_ReturnsFallback()
    {
        OutputFormatter.Parse(null, OutputFormat.Table).Should().Be(OutputFormat.Table);
        OutputFormatter.Parse(null, OutputFormat.Yaml).Should().Be(OutputFormat.Yaml);
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        OutputFormatter.Parse("JSON", OutputFormat.Table).Should().Be(OutputFormat.Json);
        OutputFormatter.Parse("Yaml", OutputFormat.Table).Should().Be(OutputFormat.Yaml);
        OutputFormatter.Parse("yml", OutputFormat.Table).Should().Be(OutputFormat.Yaml);
        OutputFormatter.Parse("table", OutputFormat.Yaml).Should().Be(OutputFormat.Table);
    }

    [Fact]
    public void Parse_Unknown_ReturnsFallback()
    {
        OutputFormatter.Parse("csv", OutputFormat.Table).Should().Be(OutputFormat.Table);
    }

    [Fact]
    public void WriteJson_PrettyPrintsToConsole()
    {
        var console = new TestConsole();
        OutputFormatter.WriteJson(new { name = "chat", version = "v1" }, console);

        console.Output.Should().Contain("\"name\":");
        console.Output.Should().Contain("chat");
    }

    [Fact]
    public void WriteYaml_EmitsCamelCase()
    {
        var console = new TestConsole();
        OutputFormatter.WriteYaml(new { AgentId = "chat", Version = "v1" }, console);

        console.Output.Should().Contain("agentId:");
        console.Output.Should().Contain("chat");
    }
}
