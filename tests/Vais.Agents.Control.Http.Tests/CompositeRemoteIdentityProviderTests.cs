// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Http;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

public sealed class CompositeRemoteIdentityProviderTests
{
    private readonly ForwardingRemoteIdentityProvider _fallback = new();

    [Fact]
    public async Task AcquireOutboundToken_UnconfiguredRuntime_UsesFallback()
    {
        var sut = new CompositeRemoteIdentityProvider(
            new Dictionary<string, IRemoteIdentityProvider>(), _fallback);

        var result = await sut.AcquireOutboundTokenAsync("https://unknown.svc", "inbound-tok");

        result.Value.Should().Be("inbound-tok"); // forwarding
    }

    [Fact]
    public async Task AcquireOutboundToken_ConfiguredRuntime_UsesProvider()
    {
        var stub = new StubIdentityProvider("exchanged-tok");
        var providers = new Dictionary<string, IRemoteIdentityProvider>
        {
            ["https://runtime-b.svc"] = stub,
        };

        var sut = new CompositeRemoteIdentityProvider(providers, _fallback);

        var result = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "inbound-tok");
        result.Value.Should().Be("exchanged-tok");
    }

    [Fact]
    public async Task AcquireOutboundToken_TrailingSlashNormalised()
    {
        var stub = new StubIdentityProvider("token-from-stub");
        var providers = new Dictionary<string, IRemoteIdentityProvider>
        {
            ["https://runtime-b.svc"] = stub,
        };

        var sut = new CompositeRemoteIdentityProvider(providers, _fallback);

        var result = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc/", "inbound");
        result.Value.Should().Be("token-from-stub");
    }

    private sealed class StubIdentityProvider(string token) : IRemoteIdentityProvider
    {
        public ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
            string runtimeUrl, string? inboundBearerToken, CancellationToken ct = default)
            => new(new OutboundCredential("Bearer", token));
    }
}
