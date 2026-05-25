// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// PG-12: RBAC and approval gate coverage for the C# DLL plugin endpoints.
/// Each test builds its own isolated host so approval state never leaks between cases.
/// </summary>
public sealed class PluginGovernanceTests
{
    private const string ManifestJson =
        """{"id":"test-plugin","version":"1.0","spec":{"language":"csharp"}}""";

    private static MultipartFormDataContent BuildPluginForm()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(ManifestJson), "manifest");
        form.Add(new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8]), "dll", "test.dll");
        return form;
    }

    private static IHost BuildHost(
        IAgentPolicyEngine? policy = null,
        IApprovalGate? approvalGate = null,
        IAssemblyDllPusher? pusher = null,
        IPluginReloader? reloader = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    if (policy is not null)
                        services.AddSingleton<IAgentPolicyEngine>(policy);
                    if (approvalGate is not null)
                        services.AddSingleton<IApprovalGate>(approvalGate);
                    if (pusher is not null)
                        services.AddSingleton<IAssemblyDllPusher>(pusher);
                    if (reloader is not null)
                        services.AddSingleton<IPluginReloader>(reloader);
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapPluginControlPlane());
                });
            })
            .Build();
    }

    // ── POST /v1/plugins (csharp branch) ─────────────────────────────────────

    [Fact]
    public async Task Apply_DeniedByRbac_Returns403()
    {
        var host = BuildHost(policy: new DenyPolicy());
        await host.StartAsync();
        var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        using var response = await client.PostAsync("/v1/plugins", BuildPluginForm());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await host.StopAsync();
        host.Dispose();
    }

    [Fact]
    public async Task Apply_WithApprovalGate_Returns202_AndDoesNotCallPusher()
    {
        var store = new InMemoryApprovalStore();
        var gate = new ApprovalGate(store);
        var pusher = new SpyPusher();
        var host = BuildHost(approvalGate: gate, pusher: pusher);
        await host.StartAsync();
        var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        using var response = await client.PostAsync("/v1/plugins", BuildPluginForm());
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        body.Should().Contain("requestId", "the response must carry the pending request id");
        body.Should().Contain("pending-approval");
        pusher.CallCount.Should().Be(0, "the DLL must not be pushed until the request is approved");

        await host.StopAsync();
        host.Dispose();
    }

    [Fact]
    public async Task Apply_AfterApproval_Proceeds_And_Returns200Or201()
    {
        var store = new InMemoryApprovalStore();
        var gate = new ApprovalGate(store);
        var pusher = new SpyPusher();
        var host = BuildHost(approvalGate: gate, pusher: pusher);
        await host.StartAsync();
        var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        // First attempt is held — triggers pending approval.
        using var held = await client.PostAsync("/v1/plugins", BuildPluginForm());
        held.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var pending = await store.ListAsync(ApprovalStatus.Pending);
        pending.Should().ContainSingle();
        await store.DecideAsync(pending[0].RequestId, approve: true, "operator");

        // Re-apply with identical form bytes → same DLL hash → same canonical → approved.
        using var approved = await client.PostAsync("/v1/plugins", BuildPluginForm());
        approved.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        pusher.CallCount.Should().Be(1);

        await host.StopAsync();
        host.Dispose();
    }

    // ── DELETE /v1/plugins/{name} ─────────────────────────────────────────────

    [Fact]
    public async Task Delete_DeniedByRbac_Returns403_AndDoesNotCallReloader()
    {
        var reloader = new SpyReloader();
        var host = BuildHost(policy: new DenyPolicy(), reloader: reloader);
        await host.StartAsync();
        var client = host.GetTestClient();
        client.BaseAddress ??= new Uri("http://localhost");

        using var response = await client.DeleteAsync("/v1/plugins/test-plugin");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        reloader.CallCount.Should().Be(0);

        await host.StopAsync();
        host.Dispose();
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class DenyPolicy : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(
            PolicyOperation op, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken ct = default)
            => ValueTask.FromResult(PolicyDecision.Deny("policy denied in test"));
    }

    private sealed class SpyPusher : IAssemblyDllPusher
    {
        public int CallCount { get; private set; }

        public Task<AssemblyDllPushResult> PushAsync(
            string pluginName, Stream dllStream, string contentType, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(
                new AssemblyDllPushResult(pluginName, AssemblyDllPushStatus.Bootstrapped, null, "0.27", null));
        }

        public Task<AssemblyDllPushResult> ImportExistingAsync(string pluginName, CancellationToken ct = default)
            => Task.FromResult(new AssemblyDllPushResult(pluginName, AssemblyDllPushStatus.Success, null, "0.27", null));
    }

    private sealed class SpyReloader : IPluginReloader
    {
        public int CallCount { get; private set; }

        public Task<PluginReloadResult> ReloadAsync(string pluginPath, CancellationToken ct = default)
            => Task.FromResult(new PluginReloadResult(null, null, PluginReloadStatus.Success, null, null));

        public Task<PluginUnloadResult> UnloadAsync(string pluginName, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new PluginUnloadResult(pluginName, null, PluginUnloadStatus.Success, null));
        }
    }
}
