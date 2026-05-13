// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Spectre.Console.Testing;
using Vais.Agents.Control.Http;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class ProblemDetailsParserTests
{
    [Fact]
    public void HandleAndExitCode_Status401_ReturnsAuthFailure()
    {
        var ex = new AgentControlPlaneException(401, type: null, title: "Unauthorized", detail: "token expired");
        var console = new TestConsole();
        ProblemDetailsParser.HandleAndExitCode(ex, console).Should().Be(ProblemDetailsParser.ExitAuthFailure);
    }

    [Fact]
    public void HandleAndExitCode_Status403WithPolicyUrn_ReturnsPolicyDenied()
    {
        var ex = new AgentControlPlaneException(403, type: ProblemDetailsParser.PolicyDeniedUrn, title: "Forbidden", detail: "cross-tenant denied");
        var console = new TestConsole();
        ProblemDetailsParser.HandleAndExitCode(ex, console).Should().Be(ProblemDetailsParser.ExitPolicyDenied);
    }

    [Fact]
    public void HandleAndExitCode_Status500_ReturnsApiError()
    {
        var ex = new AgentControlPlaneException(500, type: null, title: "Internal", detail: "boom");
        var console = new TestConsole();
        ProblemDetailsParser.HandleAndExitCode(ex, console).Should().Be(ProblemDetailsParser.ExitApiError);
    }

    [Fact]
    public void IsConflict_Status409_True()
    {
        var ex = new AgentControlPlaneException(409, type: null, title: "Conflict", detail: "already exists");
        ProblemDetailsParser.IsConflict(ex).Should().BeTrue();
    }

    [Fact]
    public void IsConflict_Status500_False()
    {
        var ex = new AgentControlPlaneException(500, type: null, title: null, detail: null);
        ProblemDetailsParser.IsConflict(ex).Should().BeFalse();
    }

    [Fact]
    public void HandleAndExitCode_WritesTitleAndDetailToConsole()
    {
        var ex = new AgentControlPlaneException(403, type: ProblemDetailsParser.PolicyDeniedUrn, title: "Forbidden", detail: "cross-tenant denied");
        var console = new TestConsole();
        ProblemDetailsParser.HandleAndExitCode(ex, console);

        console.Output.Should().Contain("Forbidden");
        console.Output.Should().Contain("cross-tenant denied");
        console.Output.Should().Contain("urn:vais-agents:policy-denied");
    }
}
