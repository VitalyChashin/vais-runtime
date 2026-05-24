// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Vais.Agents.Control.Mcp.Server.Tests;

/// <summary>
/// ND-9 guard — token gate on /design-mcp. Uses a test authentication handler so
/// we don't need a live JWT issuer; the handler accepts <c>Authorization: Test user</c>
/// and returns NoResult when no header is present.
/// </summary>
public sealed class DesignMcpServerAuthTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMcpDesignServer();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapMcpDesignServer().RequireAuthorization());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task DesignMcp_UnauthenticatedRequest_IsRejected()
    {
        using var response = await _http.PostAsync(
            "/design-mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "unauthenticated callers must be rejected at the token gate");
    }

    [Fact]
    public async Task DesignMcp_AuthenticatedRequest_IsAccepted()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "design-agent");
        using var response = await _http.PostAsync(
            "/design-mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "a valid token must be accepted at the token gate");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
                return Task.FromResult(AuthenticateResult.NoResult());

            var raw = header.ToString();
            const string prefix = "Test ";
            if (!raw.StartsWith(prefix, StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var sub = raw[prefix.Length..];
            var identity = new ClaimsIdentity([new Claim("sub", sub)], authenticationType: "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
