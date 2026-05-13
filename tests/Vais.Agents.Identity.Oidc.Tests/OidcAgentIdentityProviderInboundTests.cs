// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Identity.Oidc.Tests;

public sealed class OidcAgentIdentityProviderInboundTests : IDisposable
{
    private const string TestIssuer = "https://keycloak.test/realms/test";

    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly OidcAgentIdentityProvider _provider;

    public OidcAgentIdentityProviderInboundTests()
    {
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa);

        var config = new OpenIdConnectConfiguration { Issuer = TestIssuer };
        config.SigningKeys.Add(_signingKey);

        _provider = BuildProvider(config);
    }

    [Fact]
    public async Task ValidJwt_ReturnsPrincipal_WithSubTenantAndScopes()
    {
        var token = CreateToken("user-42", tenantId: "tenant-99", scopes: "read write");
        var request = MakeRequest($"Bearer {token}");

        var principal = await _provider.AuthenticateInboundAsync(request);

        principal.Id.Should().Be("user-42");
        principal.TenantId.Should().Be("tenant-99");
        principal.Scopes.Should().BeEquivalentTo(["read", "write"]);
    }

    [Fact]
    public async Task BearerPrefix_IsStripped_BeforeValidation()
    {
        var token = CreateToken("user-1");
        var principal = await _provider.AuthenticateInboundAsync(MakeRequest($"Bearer {token}"));
        principal.Id.Should().Be("user-1");
    }

    [Fact]
    public async Task RawToken_WithoutBearerPrefix_IsAccepted()
    {
        var token = CreateToken("user-2");
        var principal = await _provider.AuthenticateInboundAsync(MakeRequest(token));
        principal.Id.Should().Be("user-2");
    }

    [Fact]
    public async Task NoTenantOrScopes_ReturnsPrincipalWithNulls()
    {
        var token = CreateToken("user-3");
        var principal = await _provider.AuthenticateInboundAsync(MakeRequest($"Bearer {token}"));

        principal.Id.Should().Be("user-3");
        principal.TenantId.Should().BeNull();
        principal.Scopes.Should().BeNull();
    }

    [Fact]
    public async Task MissingAuthorizationMetadata_ThrowsUnauthorizedAccessException()
    {
        var request = new AgentInvocationRequest("hello");

        await FluentActions
            .Awaiting(() => _provider.AuthenticateInboundAsync(request).AsTask())
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task EmptyBearerValue_ThrowsUnauthorizedAccessException()
    {
        await FluentActions
            .Awaiting(() => _provider.AuthenticateInboundAsync(MakeRequest("Bearer ")).AsTask())
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WrongSigningKey_ThrowsUnauthorizedAccessException()
    {
        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = TestIssuer,
            Subject = new ClaimsIdentity([new Claim("sub", "user-x")]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256),
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        await FluentActions
            .Awaiting(() => _provider.AuthenticateInboundAsync(MakeRequest($"Bearer {token}")).AsTask())
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExpiredJwt_ThrowsUnauthorizedAccessException()
    {
        var config = new OpenIdConnectConfiguration { Issuer = TestIssuer };
        config.SigningKeys.Add(_signingKey);
        var provider = BuildProvider(config, clockSkew: TimeSpan.Zero);

        var token = CreateToken("user-exp", expires: DateTime.UtcNow.AddHours(-1));

        await FluentActions
            .Awaiting(() => provider.AuthenticateInboundAsync(MakeRequest($"Bearer {token}")).AsTask())
            .Should()
            .ThrowAsync<UnauthorizedAccessException>();

        provider.Dispose();
    }

    [Fact]
    public async Task ValidJwt_ScopesViaScp_AreMapped()
    {
        var token = CreateTokenWithScp("user-scp", scp: "agent:invoke graph:run");
        var principal = await _provider.AuthenticateInboundAsync(MakeRequest($"Bearer {token}"));
        principal.Scopes.Should().BeEquivalentTo(["agent:invoke", "graph:run"]);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _rsa.Dispose();
    }

    private OidcAgentIdentityProvider BuildProvider(
        OpenIdConnectConfiguration config,
        TimeSpan? clockSkew = null)
    {
        var configManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        var options = Options.Create(new OidcAgentIdentityOptions
        {
            Authority = TestIssuer,
            ClientId = "test-client",
            ValidateIssuer = true,
            ValidateAudience = false,
            ClockSkew = clockSkew ?? TimeSpan.FromSeconds(30),
        });
        return new OidcAgentIdentityProvider(
            new HttpClient(),
            configManager,
            options,
            new NullSecretResolver());
    }

    private string CreateToken(
        string sub,
        string? tenantId = null,
        string? scopes = null,
        DateTime? expires = null)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (tenantId is not null) claims.Add(new Claim("tid", tenantId));
        if (scopes is not null) claims.Add(new Claim("scope", scopes));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = TestIssuer,
            Subject = new ClaimsIdentity(claims),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private string CreateTokenWithScp(string sub, string scp)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = TestIssuer,
            Subject = new ClaimsIdentity([new Claim("sub", sub), new Claim("scp", scp)]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static AgentInvocationRequest MakeRequest(string authValue) =>
        new("hello", Metadata: new Dictionary<string, string>
        {
            [AgentInvocationMetadataKeys.Authorization] = authValue,
        });

    private sealed class NullSecretResolver : ISecretResolver
    {
        public ValueTask<string> ResolveAsync(string secretUri, CancellationToken ct = default)
            => ValueTask.FromResult(string.Empty);
    }
}
