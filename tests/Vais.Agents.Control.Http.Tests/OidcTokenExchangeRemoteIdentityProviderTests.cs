// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

public sealed class OidcTokenExchangeRemoteIdentityProviderTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly StubSecretResolver _secrets = new();

    private static RemoteRuntimeOptions TokenExchangeOptions() => new()
    {
        IdentityMode = RemoteIdentityMode.TokenExchange,
        TokenExchangeEndpoint = new Uri("https://sts.example.com/token"),
        ClientId = "vais-runtime-a",
        ClientSecretRef = "secret://env/OIDC_SECRET",
        Audience = "vais-runtime-b",
    };

    private OidcTokenExchangeRemoteIdentityProvider Create(
        RemoteRuntimeOptions? options = null,
        Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
    {
        _secrets.SetSecret("secret://env/OIDC_SECRET", "super-secret");
        var stubHandler = new StubHttpMessageHandler(handler ?? (_ => SuccessResponse()));
        var client = new HttpClient(stubHandler);
        return new OidcTokenExchangeRemoteIdentityProvider(
            client, _secrets, options ?? TokenExchangeOptions(), _time);
    }

    private static HttpResponseMessage SuccessResponse(string accessToken = "exchanged-tok-456", int expiresIn = 3600)
        => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = accessToken,
                expires_in = expiresIn,
                token_type = "Bearer",
            }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };

    [Fact]
    public async Task AcquireOutboundToken_ExchangeSuccess_ReturnsExchangedToken()
    {
        string? capturedBody = null;
        var sut = Create(handler: req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return SuccessResponse();
        });

        var result = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "inbound-tok-123");

        result.Kind.Should().Be("Bearer");
        result.Value.Should().Be("exchanged-tok-456");
        result.ExpiresAt.Should().NotBeNull();

        capturedBody.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange");
        capturedBody.Should().Contain("subject_token=inbound-tok-123");
        capturedBody.Should().Contain("client_id=vais-runtime-a");
        capturedBody.Should().Contain("audience=vais-runtime-b");
    }

    [Fact]
    public async Task AcquireOutboundToken_CachesToken_SubsequentCall()
    {
        var calls = 0;
        var sut = Create(handler: _ =>
        {
            calls++;
            return SuccessResponse();
        });

        var first = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "inbound-tok");
        var second = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "inbound-tok");

        first.Value.Should().Be(second.Value);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task AcquireOutboundToken_RerefreshesToken_AfterExpiry()
    {
        var calls = 0;
        var sut = Create(handler: _ =>
        {
            calls++;
            return SuccessResponse($"token-v{calls}", expiresIn: 60);
        });

        var first = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "inbound-tok");
        calls.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(31)); // 60s - 30s safety margin = 30s threshold
        var second = await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "inbound-tok");

        second.Value.Should().Be("token-v2");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task AcquireOutboundToken_StsReturns401_Throws()
    {
        var sut = Create(handler: _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid_client"),
        });

        var act = async () => await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", "tok");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Token exchange failed*");
    }

    [Fact]
    public async Task AcquireOutboundToken_DefaultsAudienceToRuntimeUrl()
    {
        string? capturedAudience = null;
        var sut = Create(options: new RemoteRuntimeOptions
        {
            IdentityMode = RemoteIdentityMode.TokenExchange,
            TokenExchangeEndpoint = new Uri("https://sts.example.com/token"),
            ClientId = "vais-a",
            ClientSecretRef = "secret://env/OIDC_SECRET",
        }, handler: req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var pairs = body.Split('&');
            var audPair = pairs.First(p => p.StartsWith("audience="));
            capturedAudience = Uri.UnescapeDataString(audPair["audience=".Length..]);
            return SuccessResponse();
        });

        await sut.AcquireOutboundTokenAsync("https://my-runtime.svc", "tok");
        capturedAudience.Should().Be("https://my-runtime.svc");
    }

    [Fact]
    public async Task AcquireOutboundToken_NullSubjectToken_SendsEmptySubjectToken()
    {
        string? capturedSubject = null;
        var sut = Create(handler: req =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            var pairs = body.Split('&');
            var subPair = pairs.First(p => p.StartsWith("subject_token="));
            capturedSubject = Uri.UnescapeDataString(subPair["subject_token=".Length..]);
            return SuccessResponse();
        });

        await sut.AcquireOutboundTokenAsync("https://runtime-b.svc", inboundBearerToken: null);
        capturedSubject.Should().BeEmpty();
    }
}

internal sealed class StubSecretResolver : ISecretResolver
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public void SetSecret(string uri, string value) => _secrets[uri] = value;

    public ValueTask<string> ResolveAsync(string secretUri, CancellationToken cancellationToken = default)
    {
        if (_secrets.TryGetValue(secretUri, out var value))
            return new ValueTask<string>(value);
        throw new SecretNotFoundException(secretUri);
    }
}
