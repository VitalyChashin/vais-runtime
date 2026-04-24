// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

public sealed class PyprojectTomlReaderTests
{
    private readonly PyprojectTomlReader _sut = new();

    [Fact]
    public void Read_ValidSection_ParsesApiVersionAndTools()
    {
        var toml = """
            [project]
            name = "research-planner"
            version = "0.1.0"
            requires-python = ">=3.13"

            [tool.vais.plugin]
            targetApiVersion = "0.23"
            tools = ["decompose_task", "score_plan_completeness", "summarize_findings"]
            """;

        var section = _sut.Read(toml);

        section.Should().NotBeNull();
        section!.TargetApiVersion.Should().Be("0.23");
        section.Tools.Should().BeEquivalentTo(
            new[] { "decompose_task", "score_plan_completeness", "summarize_findings" },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void Read_MissingVaisPluginSection_ReturnsNull()
    {
        var toml = """
            [project]
            name = "plain-project"

            [tool.other]
            key = "value"
            """;

        var section = _sut.Read(toml);

        section.Should().BeNull();
    }

    [Fact]
    public void Read_MissingToolSection_ReturnsNull()
    {
        var toml = """
            [project]
            name = "plain-project"
            """;

        var section = _sut.Read(toml);

        section.Should().BeNull();
    }

    [Fact]
    public void Read_EmptyToolsList_ReturnsSectionWithNoTools()
    {
        var toml = """
            [tool.vais.plugin]
            targetApiVersion = "0.23"
            tools = []
            """;

        var section = _sut.Read(toml);

        section.Should().NotBeNull();
        section!.Tools.Should().BeEmpty();
    }

    [Fact]
    public void Read_MissingToolsList_ReturnsSectionWithNoTools()
    {
        var toml = """
            [tool.vais.plugin]
            targetApiVersion = "0.23"
            """;

        var section = _sut.Read(toml);

        section.Should().NotBeNull();
        section!.Tools.Should().BeEmpty();
    }

    [Fact]
    public void Read_MultiLineToolsArray_ParsesAllEntries()
    {
        var toml = """
            [tool.vais.plugin]
            targetApiVersion = "0.23"
            tools = [
              "decompose_task",
              "score_plan_completeness",
              "summarize_findings"
            ]
            """;

        var section = _sut.Read(toml);

        section.Should().NotBeNull();
        section!.Tools.Should().BeEquivalentTo(
            new[] { "decompose_task", "score_plan_completeness", "summarize_findings" },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void Read_MalformedArraySyntax_Throws()
    {
        // A tools array that starts but never closes causes a FormatException.
        var toml =
            "[tool.vais.plugin]\ntargetApiVersion = \"0.23\"\ntools = [\"a\", \"b\"\n";

        var act = () => _sut.Read(toml);

        act.Should().Throw<FormatException>("an unclosed tools array is invalid");
    }

    [Fact]
    public void Read_ContentWithoutVaisSection_ReturnsNull()
    {
        // Content with no [tool.vais.plugin] section returns null rather than throwing.
        var toml = "[some_table]\nkey = value";

        var section = _sut.Read(toml);

        section.Should().BeNull();
    }

    [Fact]
    public void Read_QuotedEntryPoints_DoesNotAffectPluginSection()
    {
        // Verifies that the quoted-key header [project.entry-points."vais.plugins"]
        // does not confuse the reader when [tool.vais.plugin] is also present.
        var toml = """
            [tool.vais.plugin]
            targetApiVersion = "0.23"
            tools = ["my_tool"]

            [project.entry-points."vais.plugins"]
            server = "research_planner.server:main"
            """;

        var section = _sut.Read(toml);

        section.Should().NotBeNull();
        section!.TargetApiVersion.Should().Be("0.23");
        section.Tools.Should().ContainSingle().Which.Should().Be("my_tool");
    }
}
