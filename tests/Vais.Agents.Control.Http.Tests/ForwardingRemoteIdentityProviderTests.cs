// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

public sealed class ForwardingRemoteIdentityProviderTests
{
    private readonly ForwardingRemoteIdentityProvider _sut = new();

    [Fact]
    public async Task AcquireOutboundToken_ReturnsInboundBearerToken()
    {
        var result = await _sut.AcquireOutboundTokenAsync(
            "https://runtime-b.svc",
            "tok-abc");

        result.Kind.Should().Be("Bearer");
        result.Value.Should().Be("tok-abc");
        result.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task AcquireOutboundToken_NullToken_ReturnsEmptyValue()
    {
        var result = await _sut.AcquireOutboundTokenAsync(
            "https://runtime-b.svc",
            inboundBearerToken: null);

        result.Kind.Should().Be("Bearer");
        result.Value.Should().BeEmpty();
        result.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task AcquireOutboundToken_RuntimeUrl_IsIgnored()
    {
        var result1 = await _sut.AcquireOutboundTokenAsync("https://a.svc", "tok");
        var result2 = await _sut.AcquireOutboundTokenAsync("https://b.svc", "tok");

        result1.Value.Should().Be(result2.Value);
    }
}
