// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Protocols.A2A.Server.Tests;

/// <summary>
/// v0.8 PR 3 HTTP integration: JWT auth + dual-header forwarding on the A2A
/// endpoints. Exercises a test authentication handler that replicates the
/// dual-header logic from <see cref="A2AAgentServerJwtAuthExtensions.AddA2AAgentServerJwtAuth"/>
/// end-to-end — same shape as the MCP server's auth tests.
/// </summary>
public sealed class A2AAgentServerJwtAuthTests
{
    [Fact]
    public async Task Jwt_Auth_Pipeline_Rejects_Anonymous_Requests_On_Secured_Endpoint()
    {
        using var host = await BuildHost(requireAuth: true);
        using var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        using var response = await client.PostAsync("/agents/echo",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await host.StopAsync();
    }

    [Fact]
    public async Task Jwt_Auth_Accepts_Authorization_Header()
    {
        using var host = await BuildHost(requireAuth: true);
        using var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "alice/acme/agent:invoke");

        using var response = await client.PostAsync("/agents/echo",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        await host.StopAsync();
    }

    [Fact]
    public async Task Jwt_Auth_Accepts_XUpstreamAuthorization_Header()
    {
        using var host = await BuildHost(requireAuth: true);
        using var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");
        client.DefaultRequestHeaders.Add("X-Upstream-Authorization", "Test upstream-alice/hub-tenant/agent:invoke");

        using var response = await client.PostAsync("/agents/echo",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        await host.StopAsync();
    }

    [Fact]
    public async Task Jwt_Auth_XUpstream_Wins_When_Both_Headers_Present()
    {
        // Gateway credential on Authorization, user credential upstream-forwarded —
        // upstream should win so downstream audit logs see the real caller.
        var observedPrincipals = new List<string?>();
        using var host = await BuildHost(
            requireAuth: true,
            onRequest: ctx => observedPrincipals.Add(ctx.User?.FindFirst("sub")?.Value));
        using var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "gateway/hub/internal");
        client.DefaultRequestHeaders.Add("X-Upstream-Authorization", "Test user-alice/acme/agent:invoke");

        using var response = await client.PostAsync("/agents/echo",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        observedPrincipals.Should().Contain(p => p == "user-alice");
        await host.StopAsync();
    }

    [Fact]
    public async Task AgentCard_SecuritySchemes_Auto_Populated_With_Bearer_JWT()
    {
        using var host = await BuildHost(requireAuth: false);
        using var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        using var response = await client.GetAsync("/agents/echo/.well-known/agent-card.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"bearer\"");
        body.Should().Contain("\"JWT\"");
        await host.StopAsync();
    }

    // ---- host builder ----

    private static Task<IHost> BuildHost(bool requireAuth, Action<HttpContext>? onRequest = null) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddRouting();
                    services.AddA2AAgentServer(opts =>
                    {
                        // Wire the bearer scheme on every derived card — same effect as
                        // AddA2AAgentServerJwtAuth's card-customizer hook. Tests that
                        // exercise the auth pipeline use a Test scheme handler below so
                        // we don't double-register JwtBearer under "A2AJwt" here.
                        var prior = opts.CustomizeCard;
                        opts.CustomizeCard = (manifest, card) =>
                        {
                            A2AAgentServerJwtAuthExtensions.AdvertiseBearer(card);
                            prior?.Invoke(manifest, card);
                        };
                    });

                    // Use a simple scheme that mirrors the dual-header contract — same approach
                    // as the MCP server's JWT auth integration tests. Real JWT validation is
                    // handled by Microsoft.AspNetCore.Authentication.JwtBearer under the
                    // A2AJwt scheme name at production use sites.
                    services.AddAuthentication(A2AAgentServerJwtAuthExtensions.A2ABearerSchemeName)
                        .AddScheme<AuthenticationSchemeOptions, UpstreamAwareTestAuthHandler>(
                            A2AAgentServerJwtAuthExtensions.A2ABearerSchemeName, _ => { });
                    services.AddAuthorization();

                });
                web.Configure(app =>
                {
                    var lifecycle = app.ApplicationServices.GetRequiredService<IAgentLifecycleManager>();
                    lifecycle.CreateAsync(new AgentManifest(
                        "echo", "1.0",
                        new AgentHandlerRef("declarative"),
                        new[] { new ProtocolBinding("A2A") },
                        Array.Empty<ToolRef>())).GetAwaiter().GetResult();

                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    if (onRequest is not null)
                    {
                        app.Use(async (ctx, next) =>
                        {
                            onRequest(ctx);
                            await next();
                        });
                    }
                    if (requireAuth)
                    {
                        // Gate the JSON-RPC routes behind authentication. The well-known
                        // agent-card endpoint stays unauthenticated because spec §7 says
                        // discovery is public.
                        app.Use(async (ctx, next) =>
                        {
                            var isAgentRoute = ctx.Request.Path.StartsWithSegments("/agents");
                            var isWellKnown = ctx.Request.Path.Value?.Contains("/.well-known/") ?? false;
                            if (isAgentRoute && !isWellKnown &&
                                (ctx.User?.Identity?.IsAuthenticated ?? false) == false)
                            {
                                ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                return;
                            }
                            await next();
                        });
                    }
                    app.UseEndpoints(endpoints => endpoints.MapA2AAgentServer("http://localhost"));
                });
            })
            .StartAsync();

    /// <summary>
    /// Test auth handler that mirrors the
    /// <see cref="A2AAgentServerJwtAuthExtensions.AddA2AAgentServerJwtAuth"/> dual-header
    /// logic end-to-end — upstream header wins, falls through to Authorization otherwise.
    /// </summary>
    private sealed class UpstreamAwareTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public UpstreamAwareTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? raw = null;
            if (Request.Headers.TryGetValue("X-Upstream-Authorization", out var upstream) && upstream.Count > 0)
            {
                raw = upstream[0];
            }
            else if (Request.Headers.TryGetValue("Authorization", out var auth) && auth.Count > 0)
            {
                raw = auth[0];
            }
            if (string.IsNullOrEmpty(raw))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            const string prefix = "Test ";
            if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.Fail("expected Test scheme"));
            }
            var parts = raw[prefix.Length..].Split('/');
            var sub = parts.Length > 0 ? parts[0] : "anon";
            var tenant = parts.Length > 1 ? parts[1] : string.Empty;
            var scope = parts.Length > 2 ? parts[2] : string.Empty;
            var claims = new List<Claim> { new("sub", sub) };
            if (!string.IsNullOrEmpty(tenant)) claims.Add(new Claim("tenant_id", tenant));
            if (!string.IsNullOrEmpty(scope)) claims.Add(new Claim("scope", scope));
            var identity = new ClaimsIdentity(claims, authenticationType: A2AAgentServerJwtAuthExtensions.A2ABearerSchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), A2AAgentServerJwtAuthExtensions.A2ABearerSchemeName)));
        }
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
