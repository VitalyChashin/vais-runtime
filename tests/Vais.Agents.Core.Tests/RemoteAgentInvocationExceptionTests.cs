// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class RemoteAgentInvocationExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var ex = new RemoteAgentInvocationException("https://runtime-b.svc", HttpStatusCode.NotFound);

        ex.RuntimeUrl.Should().Be("https://runtime-b.svc");
        ex.Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Message_DefaultsToStatusCodeFormat_WhenDetailNull()
    {
        var ex = new RemoteAgentInvocationException("https://remote", HttpStatusCode.BadGateway);

        ex.Message.Should().Contain("502");
    }

    [Fact]
    public void Message_UsesDetail_WhenProvided()
    {
        var ex = new RemoteAgentInvocationException("https://remote", HttpStatusCode.ServiceUnavailable, "downstream overloaded");

        ex.Message.Should().Contain("downstream overloaded");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void IsRetryable_ReflectsStatus(HttpStatusCode status, bool expected)
    {
        var ex = new RemoteAgentInvocationException("https://remote", status);

        ex.IsRetryable.Should().Be(expected);
    }

    [Fact]
    public void InnerException_IsPreserved()
    {
        var inner = new InvalidOperationException("transport error");
        var ex = new RemoteAgentInvocationException("https://remote", HttpStatusCode.ServiceUnavailable, inner: inner);

        ex.InnerException.Should().BeSameAs(inner);
    }
}
