// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Verifies the stdio path's error classification (parity with the HTTP container path):
/// <see cref="PythonAgentInvokeException"/> implements <see cref="IClassifiedAgentError"/>, the SDK's
/// <c>[vais.errorType=...]</c> marker is parsed, and the shim surfaces the classified errorType.
/// </summary>
public sealed class PythonAgentErrorClassificationTests
{
    [Theory]
    [InlineData("LlmGatewayError", true)]
    [InlineData("ToolError", true)]
    [InlineData("Timeout", true)]
    [InlineData("InternalError", false)]
    public void IsTransient_TrueOnlyForGatewayToolTimeout(string errorType, bool expected)
    {
        var ex = new PythonAgentInvokeException(errorType, "msg");
        ex.IsTransient.Should().Be(expected);
        ((IClassifiedAgentError)ex).ErrorType.Should().Be(errorType);
    }

    [Theory]
    [InlineData("[vais.errorType=Timeout] took too long", "Timeout")]
    [InlineData("prefix [vais.errorType=ToolError] detail", "ToolError")]
    [InlineData("no marker here", null)]
    [InlineData("[vais.errorType=", null)]
    [InlineData("", null)]
    public void TryParseErrorType_ExtractsMarker(string message, string? expected)
    {
        PythonAgentInvokeException.TryParseErrorType(message).Should().Be(expected);
    }

    [Fact]
    public async Task AskAsync_PropagatesClassifiedException()
    {
        var shim = new PythonAgentShim(
            new ThrowingChannel(new PythonAgentInvokeException("Timeout", "boom")),
            new InMemoryAgentSession("a"), maxStateSizeBytes: 0);

        var act = () => shim.AskAsync("hi");
        var ex = (await act.Should().ThrowAsync<PythonAgentInvokeException>()).Which;
        ex.ErrorType.Should().Be("Timeout");
        ex.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ClassifiedException_SurfacesErrorTypeInTurnFailed()
    {
        var shim = new PythonAgentShim(
            new ThrowingChannel(new PythonAgentInvokeException("LlmGatewayError", "boom")),
            new InMemoryAgentSession("a"), maxStateSizeBytes: 0);

        var events = new List<AgentEvent>();
        await foreach (var e in shim.StreamAsync("hi", new AgentContext(), CancellationToken.None))
            events.Add(e);

        events.OfType<TurnFailed>().Should().ContainSingle()
            .Which.ErrorType.Should().Be("LlmGatewayError");
    }

    private sealed class ThrowingChannel : IPythonAgentChannel
    {
        private readonly Exception _ex;

        public ThrowingChannel(Exception ex)
        {
            _ex = ex;
            Descriptor = new PythonPluginDescriptor(
                Name: "fake-plugin",
                PluginDirectory: "/fake",
                InterpreterPath: "/fake/python",
                EntrypointPath: "/fake/server.py",
                TargetApiVersion: "0.25",
                HandshakeTimeoutSeconds: 5,
                RestartPolicy: PythonRestartPolicy.Never,
                DeclaredTools: [],
                SecretRefs: new Dictionary<string, string>());
        }

        public PythonPluginDescriptor Descriptor { get; }

        public Task<AgentInvokeResponse> InvokeAgentAsync(AgentInvokeRequest request, CancellationToken ct)
            => Task.FromException<AgentInvokeResponse>(_ex);

        public async IAsyncEnumerable<AgentStreamFrame> StreamAgentAsync(
            AgentInvokeRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.FromException(_ex);
            yield break;
        }
    }
}
