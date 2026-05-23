// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Verifies <see cref="ContainerInvokeException"/> classification (EC-2) and that a 504/Timeout
/// aborts the in-flight invocation without draining the container (EC-12, decision #4).
/// </summary>
public sealed class ContainerErrorClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "LlmGatewayError", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ToolError", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, "Timeout", true)]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", false)]
    [InlineData(HttpStatusCode.UnprocessableContent, "OpaqueStateDeserializationError", false)]
    public void IsTransient_TrueOnlyForGatewayToolTimeout(HttpStatusCode status, string errorType, bool expected)
    {
        var ex = new ContainerInvokeException(status, errorType, "msg", diagnosticTail: null);
        ex.IsTransient.Should().Be(expected);
        ((IClassifiedAgentError)ex).ErrorType.Should().Be(errorType);
    }

    [Fact]
    public async Task Timeout504_Throws_AndDoesNotDrainContainer()
    {
        var supervisor = new FakeSupervisor();
        var httpClient = new HttpClient(new RoutingHandler(HttpStatusCode.GatewayTimeout, "Timeout"))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns("tok");

        var shim = new ContainerAgentShim(
            supervisor, httpClient, [],
            new AgentManifest("a", "1.0", new AgentHandlerRef("Test"), [], []),
            tokenSvc, "http://gw/llm", "http://gw/tools", invokeTimeoutSeconds: 60,
            sessionConfig: null, logger: NullLogger.Instance);

        var act = () => shim.AskAsync("hi");
        var ex = (await act.Should().ThrowAsync<ContainerInvokeException>()).Which;
        ex.ErrorType.Should().Be("Timeout");
        ex.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);

        supervisor.DrainCalls.Should().Be(0, "a 504 aborts the invocation in-flight; only a failed health check drains the container");
    }

    /// <summary>
    /// Hand-written double — <see cref="IContainerSupervisor"/> is internal and not proxyable by
    /// NSubstitute. Only <see cref="DrainAndReplaceAsync"/> is exercised by this test.
    /// </summary>
    private sealed class FakeSupervisor : IContainerSupervisor
    {
        public int DrainCalls { get; private set; }
        public ContainerPluginDescriptor Descriptor => throw new NotSupportedException();
        public ContainerPluginStatus Status => throw new NotSupportedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContainerReplaceResult> DrainAndReplaceAsync(string? newImage, CancellationToken ct)
        {
            DrainCalls++;
            return Task.FromResult<ContainerReplaceResult>(default!);
        }
        public bool TryAcquireInvoke() => throw new NotSupportedException();
        public void ReleaseInvoke() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>200 on GET /health; the configured error on POST /v1/invoke.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);
        private readonly HttpStatusCode _status;
        private readonly string _errorType;

        public RoutingHandler(HttpStatusCode status, string errorType)
        {
            _status = status;
            _errorType = errorType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery is "/health" or "/health/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = JsonContent.Create(new { errorType = _errorType, errorMessage = "boom" }, options: s_web),
            });
        }
    }
}
