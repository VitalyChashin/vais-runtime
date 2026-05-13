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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// PR 4: inbound auth + principal flow. Uses a test authentication handler so
/// we don't need a live JWT issuer in-process — the handler extracts claims from
/// a conventional <c>Authorization: Test &lt;user&gt;/&lt;tenant&gt;/&lt;scope&gt;</c>
/// header and produces the same <see cref="ClaimsPrincipal"/> the real JWT
/// middleware would. Downstream pieces (DefaultPrincipalMapper, middleware,
/// policy engine seeing the principal) are identical either way.
/// </summary>
public sealed class AgentControlPlaneAuthTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;
    private RecordingPolicy _policy = null!;
    private RecordingAudit _audit = null!;

    public async Task InitializeAsync()
    {
        _policy = new RecordingPolicy();
        _audit = new RecordingAudit();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentPolicyEngine>(_policy);
                    services.AddSingleton<IAuditLog>(_audit);
                    services.AddSingleton<AsyncLocalAgentContextAccessor>();
                    services.AddSingleton<IAgentContextAccessor>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>(),
                        sp.GetRequiredService<IAgentPolicyEngine>(),
                        sp.GetRequiredService<IAuditLog>(),
                        sp.GetRequiredService<IAgentContextAccessor>()));
                    services.AddSingleton<IPrincipalMapper, DefaultPrincipalMapper>();
                    services.AddAgentControlPlane();
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseAgentControlPlanePrincipalMapping();
                    app.UseEndpoints(endpoints => endpoints.MapAgentControlPlane());
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
    public async Task Authenticated_Request_Reaches_Policy_With_Principal()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "alice/acme/agent:invoke");

        using var response = await _http.PostAsync("/v1/agents",
            new StringContent("""{"apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"x","version":"1.0"}}""",
                System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _policy.SeenPrincipals.Should().ContainSingle();
        _policy.SeenPrincipals[0]!.Id.Should().Be("alice");
        _policy.SeenPrincipals[0]!.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task Anonymous_Request_Flows_With_Null_Principal()
    {
        // No Authorization header — test handler returns NoResult, principal stays anonymous.
        using var response = await _http.PostAsync("/v1/agents",
            new StringContent("""{"apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"y","version":"1.0"}}""",
                System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _policy.SeenPrincipals.Should().ContainSingle();
        _policy.SeenPrincipals[0].Should().BeNull();
    }

    [Fact]
    public async Task Audit_Log_Captures_Principal_Id()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "bob/beta/");

        using var response = await _http.PostAsync("/v1/agents",
            new StringContent("""{"apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"z","version":"1.0"}}""",
                System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        _audit.Entries.Should().Contain(e => e.PrincipalId == "bob" && e.TenantId == "beta");
    }

    [Fact]
    public void Default_Principal_Mapper_Extracts_Standard_Oidc_Claims()
    {
        var mapper = new DefaultPrincipalMapper();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "alice"),
            new Claim("tenant_id", "acme"),
            new Claim("scope", "agent:read agent:invoke"),
        }, authenticationType: "test");
        var user = new ClaimsPrincipal(identity);

        var principal = mapper.Map(user);

        principal.Should().NotBeNull();
        principal!.Id.Should().Be("alice");
        principal.TenantId.Should().Be("acme");
        principal.Scopes.Should().Equal("agent:read", "agent:invoke");
    }

    [Fact]
    public void Default_Principal_Mapper_Returns_Null_For_Unauthenticated()
    {
        var mapper = new DefaultPrincipalMapper();
        var principal = mapper.Map(new ClaimsPrincipal(new ClaimsIdentity())); // no auth type = unauthenticated
        principal.Should().BeNull();
    }

    [Fact]
    public void Default_Principal_Mapper_Falls_Back_To_Azure_AD_Tenant_Claim()
    {
        var mapper = new DefaultPrincipalMapper();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "alice"),
            new Claim("tid", "azure-tenant"), // Azure AD uses 'tid' instead of 'tenant_id'
        }, authenticationType: "test");

        var principal = mapper.Map(new ClaimsPrincipal(identity));
        principal!.TenantId.Should().Be("azure-tenant");
    }

    // ---- test authentication handler ----

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var raw = header.ToString();
            const string prefix = "Test ";
            if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
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

    private sealed class RecordingPolicy : IAgentPolicyEngine
    {
        public List<AgentPrincipal?> SeenPrincipals { get; } = new();
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
        {
            SeenPrincipals.Add(principal);
            return ValueTask.FromResult(PolicyDecision.Allow);
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditLogEntry> Entries { get; } = new();
        public ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
