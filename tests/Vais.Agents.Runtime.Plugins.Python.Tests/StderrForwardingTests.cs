// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Tests that <see cref="PythonSubprocessSupervisor"/> forwards subprocess stderr lines
/// to ILogger with the correct <c>plugin</c> scope.
/// </summary>
public sealed class StderrForwardingTests
{
    private static readonly TimeSpan[] FastBackoff =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];

    private static PythonPluginDescriptor MakeDescriptor(string name = "my-plugin") =>
        new(
            Name: name,
            PluginDirectory: "/fake",
            InterpreterPath: "/fake/python",
            EntrypointPath: "/fake/server.py",
            TargetApiVersion: "0.23",
            HandshakeTimeoutSeconds: 5,
            RestartPolicy: PythonRestartPolicy.Never,
            DeclaredTools: [],
            SecretRefs: new Dictionary<string, string>());

    private static (Stream SupervisorInput, Stream SupervisorOutput, Stream ResponderInput, Stream ResponderOutput) MakePipes()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        return (c2s.Writer.AsStream(), s2c.Reader.AsStream(), c2s.Reader.AsStream(), s2c.Writer.AsStream());
    }

    [Fact]
    public async Task Stderr_LinesForwardedToLogger_WithPluginScope()
    {
        var (supIn, supOut, respIn, respOut) = MakePipes();
        var handle = new FakeSubprocessHandle(supIn, supOut, stderrLines: "stderr line 1\nstderr line 2");
        var responder = new MockMcpResponder(respIn, respOut);

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var loggerFactory = new CapturingLoggerFactory();
        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor("my-plugin"),
            loggerFactory,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        await supervisor.InitialHandshakeTask;

        // ForwardStderrAsync is fire-and-forget; poll until both lines appear.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snap = loggerFactory.Logger.Entries;
            if (snap.Any(e => e.Message == "stderr line 1") &&
                snap.Any(e => e.Message == "stderr line 2"))
                break;
            await Task.Delay(10);
        }

        var entries = loggerFactory.Logger.Entries;
        entries.Should().Contain(e => e.Message == "stderr line 1", "supervisor must log stderr");
        entries.Should().Contain(e => e.Message == "stderr line 2", "supervisor must log stderr");

        // Every stderr entry must carry the plugin scope.
        var stderrEntries = entries
            .Where(e => e.Message is "stderr line 1" or "stderr line 2")
            .ToList();

        stderrEntries.Should().AllSatisfy(e =>
            e.Scope.Should().ContainKey("plugin").WhoseValue.Should().Be("my-plugin"));

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }
}
