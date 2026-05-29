// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.ScriptRuntime.Tests;

/// <summary>CM-3 — the factory that builds run_code and applies the toolset filter.</summary>
public sealed class CodeModeToolFactoryTests
{
    private static CodeModeToolFactory Build() => new(
        new RawMcpClientGenerator(),
        Substitute.For<IScriptRuntimeClient>(),
        Substitute.For<ICallTokenService>(),
        new ScriptRuntimeOptions(),
        NullLoggerFactory.Instance);

    [Fact]
    public void Create_ReturnsRunCodeTool_OverAllTools()
    {
        var tool = Build().Create("agent-1", new CodeModeSpec { Enabled = true },
            [new FakeTool("crm"), new FakeTool("warehouse")]);

        tool.Name.Should().Be("run_code");
        tool.Description.Should().Contain("crm").And.Contain("warehouse");
    }

    [Fact]
    public void Create_ToolsetFilter_NarrowsTheGeneratedApi()
    {
        var tool = Build().Create("agent-1",
            new CodeModeSpec { Enabled = true, Toolset = ["crm"] },
            [new FakeTool("crm"), new FakeTool("warehouse")]);

        tool.Description.Should().Contain("crm");
        tool.Description.Should().NotContain("warehouse");
    }
}
