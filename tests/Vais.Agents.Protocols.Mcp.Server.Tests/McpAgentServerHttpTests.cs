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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Protocols.Mcp.Server.Tests;

/// <summary>
/// v0.7 PR 2 HTTP integration: end-to-end via ASP.NET Core TestHost. The MCP
/// streamable-HTTP wire is complex enough that we don't reimplement a client;
/// instead, we verify the route is mounted, auth pipelines gate as expected,
/// the dual-header shape works, and the types our builder registers end up in
/// DI correctly.
/// </summary>
public sealed class McpAgentServerHttpTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = await BuildHost(withAuth: false);
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
    public async Task Mcp_Endpoint_Mounted_At_Configured_Pattern()
    {
        // MCP streamable-HTTP expects a POST with an initialize JSON-RPC body.
        // We just verify the route exists (non-404) — exact MCP handshake is
        // the SDK's responsibility.
        using var response = await _http.PostAsync("/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddMcpAgentServerHttp_Registers_McpServer_In_DI()
    {
        using var scope = _host.Services.CreateScope();
        // The SDK registers McpServerOptions — that's the contract we depend on.
        var optionsSnapshot = scope.ServiceProvider.GetService<IOptions<ModelContextProtocol.Server.McpServerOptions>>();
        optionsSnapshot.Should().NotBeNull();
        optionsSnapshot!.Value.ServerInfo.Should().NotBeNull();
        optionsSnapshot.Value.ServerInfo!.Name.Should().Be("test-mcp-server");
        optionsSnapshot.Value.Handlers.ListToolsHandler.Should().NotBeNull();
        optionsSnapshot.Value.Handlers.CallToolHandler.Should().NotBeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Jwt_Auth_Pipeline_Rejects_Anonymous_Requests_On_Secured_Endpoint()
    {
        using var secured = await BuildHost(withAuth: true, requireAuth: true);
        using var client = secured.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        using var response = await client.PostAsync("/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await secured.StopAsync();
    }

    [Fact]
    public async Task Jwt_Auth_Accepts_Authorization_Header()
    {
        using var secured = await BuildHost(withAuth: true, requireAuth: true);
        using var client = secured.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "alice/acme/agent:invoke");

        using var response = await client.PostAsync("/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        await secured.StopAsync();
    }

    [Fact]
    public async Task Jwt_Auth_Accepts_XUpstreamAuthorization_Header()
    {
        using var secured = await BuildHost(withAuth: true, requireAuth: true);
        using var client = secured.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");
        // No Authorization header; upstream header should be accepted.
        client.DefaultRequestHeaders.Add("X-Upstream-Authorization", "Test upstream-alice/hub-tenant/agent:invoke");

        using var response = await client.PostAsync("/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        await secured.StopAsync();
    }

    [Fact]
    public async Task Jwt_Auth_XUpstream_Wins_When_Both_Headers_Present()
    {
        // Validate that when both headers arrive, X-Upstream-Authorization is the
        // one the pipeline trusts — matches ContextForge's forwarding convention
        // (gateway's own credential on Authorization, user credential upstream-forwarded).
        var observedPrincipals = new List<string?>();
        using var secured = await BuildHost(
            withAuth: true,
            requireAuth: true,
            onRequest: ctx => observedPrincipals.Add(ctx.User?.FindFirst("sub")?.Value));
        using var client = secured.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "gateway/hub/internal");
        client.DefaultRequestHeaders.Add("X-Upstream-Authorization", "Test user-alice/acme/agent:invoke");

        using var response = await client.PostAsync("/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        observedPrincipals.Should().NotBeEmpty();
        observedPrincipals.Should().Contain(p => p == "user-alice");
        await secured.StopAsync();
    }

    // ---- helpers ----

    private static Task<IHost> BuildHost(bool withAuth, bool requireAuth = false, Action<HttpContext>? onRequest = null) =>
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
                    services.AddMcpAgentServerHttp(o =>
                    {
                        o.Name = "test-mcp-server";
                        o.Version = "0.7";
                    });
                    if (withAuth)
                    {
                        services.AddAuthentication("Test")
                            .AddScheme<AuthenticationSchemeOptions, UpstreamAwareTestAuthHandler>("Test", _ => { });
                        services.AddAuthorization();
                    }
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    if (withAuth)
                    {
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
                    }
                    app.UseEndpoints(endpoints =>
                    {
                        var mcp = endpoints.MapMcpAgentServer("/mcp");
                        if (requireAuth)
                        {
                            mcp.RequireAuthorization();
                        }
                    });
                });
            })
            .StartAsync();

    /// <summary>
    /// Test auth handler that mirrors the <see cref="HttpAgentServerExtensions.AddMcpAgentServerJwtAuth"/>
    /// dual-header logic end-to-end — upstream header wins, falls through to
    /// Authorization otherwise.
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
                return Task.FromResult(AuthenticateResult.Fail("no credentials"));
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
            var identity = new ClaimsIdentity(claims, authenticationType: "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
