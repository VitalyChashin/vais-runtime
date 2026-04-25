// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Vais.Agents.Control;
using Vais.Agents.Identity.Oidc;
using Xunit;

namespace Vais.Agents.Identity.Oidc.Tests;

public sealed class OidcAgentIdentityProviderOutboundTests : IDisposable
{
    private const string TokenEndpoint = "https://keycloak.test/realms/test/protocol/openid-connect/token";
    private const string ClientId = "svc-agent";
    private const string CredentialRef = "secret://env/KEYCLOAK_CLIENT_SECRET";
    private const string ClientSecret = "super-secret";

    private readonly OidcAgentIdentityProvider _provider;
    private readonly RecordingHttpMessageHandler _handler;
    private readonly FakeTimeProvider _time;

    public OidcAgentIdentityProviderOutboundTests()
    {
        var config = new OpenIdConnectConfiguration { TokenEndpoint = TokenEndpoint };
        var configManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);

        var options = Options.Create(new OidcAgentIdentityOptions
        {
            Authority = "https://keycloak.test/realms/test",
            ClientId = ClientId,
        });

        _handler = new RecordingHttpMessageHandler();
        _time = new FakeTimeProvider();

        _provider = new OidcAgentIdentityProvider(
            new HttpClient(_handler),
            configManager,
            options,
            new StubSecretResolver(ClientSecret),
            _time);
    }

    [Fact]
    public async Task ClientCredentials_ReturnsAccessToken()
    {
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-1","expires_in":300,"token_type":"Bearer"}""");

        var cred = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);

        cred.Kind.Should().Be("Bearer");
        cred.Value.Should().Be("tok-1");
        cred.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ClientCredentials_RequestBody_ContainsExpectedFields()
    {
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-2","expires_in":300,"token_type":"Bearer"}""");

        _ = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);

        _handler.LastRequestBody.Should().Contain("grant_type=client_credentials");
        _handler.LastRequestBody.Should().Contain($"client_id={Uri.EscapeDataString(ClientId)}");
        _handler.LastRequestBody.Should().Contain($"client_secret={Uri.EscapeDataString(ClientSecret)}");
    }

    [Fact]
    public async Task CacheHit_ShortCircuitsSecondHttpCall()
    {
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-3","expires_in":300,"token_type":"Bearer"}""");

        var first = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);
        var second = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);

        first.Value.Should().Be("tok-3");
        second.Value.Should().Be("tok-3");
        _handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ExpiredToken_RefreshesFromTokenEndpoint()
    {
        // expires_in=60, safety margin=30s, so effective window=30s
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-old","expires_in":60,"token_type":"Bearer"}""");
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-new","expires_in":300,"token_type":"Bearer"}""");

        var first = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);
        _time.Advance(TimeSpan.FromSeconds(35));
        var second = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);

        first.Value.Should().Be("tok-old");
        second.Value.Should().Be("tok-new");
        _handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task DifferentAgents_HaveIsolatedCacheEntries()
    {
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-a","expires_in":300,"token_type":"Bearer"}""");
        _handler.EnqueueResponse(HttpStatusCode.OK,
            """{"access_token":"tok-b","expires_in":300,"token_type":"Bearer"}""");

        var credA = await _provider.AcquireOutboundAsync("agent-a", CredentialRef);
        var credB = await _provider.AcquireOutboundAsync("agent-b", CredentialRef);

        credA.Value.Should().Be("tok-a");
        credB.Value.Should().Be("tok-b");
        _handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task FailedTokenEndpoint_ThrowsInvalidOperationException()
    {
        _handler.EnqueueResponse(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");

        await FluentActions
            .Awaiting(() => _provider.AcquireOutboundAsync("agent-a", CredentialRef).AsTask())
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*401*");
    }

    public void Dispose() => _provider.Dispose();

    private sealed class StubSecretResolver(string value) : ISecretResolver
    {
        public ValueTask<string> ResolveAsync(string secretUri, CancellationToken ct = default)
            => ValueTask.FromResult(value);
    }

    internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public int RequestCount { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void EnqueueResponse(HttpStatusCode status, string body)
            => _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected HTTP call — no response enqueued.");

            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
