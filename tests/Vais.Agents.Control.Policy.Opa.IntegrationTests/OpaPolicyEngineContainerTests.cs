// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.Policy.Opa;
using Xunit;

namespace Vais.Agents.Control.Policy.Opa.IntegrationTests;

/// <summary>
/// End-to-end integration against a real <c>openpolicyagent/opa:1.15.2</c>
/// container. Each test mounts its own Rego fixture so policy behaviour
/// is isolated. "Container" in the class name excludes these from the
/// non-container test bucket per the shipped filter convention
/// (<c>--filter "FullyQualifiedName!~Container"</c>).
/// </summary>
public sealed class OpaPolicyEngineContainerTests
{
    private static string FixturePath(string filename)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", filename);

    [Fact]
    public async Task AllowAllPolicy_InvokeVerb_Allows()
    {
        await using var opa = new OpaContainer(FixturePath("allow-all.rego"));
        await opa.StartAsync();

        var engine = BuildEngine(opa.BaseUrl);
        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal("tenant-42"));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task TenantScopedPolicy_MatchingTenant_Allows()
    {
        await using var opa = new OpaContainer(FixturePath("tenant-scoped.rego"));
        await opa.StartAsync();

        var engine = BuildEngine(opa.BaseUrl);
        var manifest = SampleManifestWithLabel("tenant", "tenant-42");
        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, manifest, SamplePrincipal("tenant-42"));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task TenantScopedPolicy_CrossTenant_DeniesWithReason()
    {
        await using var opa = new OpaContainer(FixturePath("tenant-scoped.rego"));
        await opa.StartAsync();

        var engine = BuildEngine(opa.BaseUrl);
        var manifest = SampleManifestWithLabel("tenant", "tenant-OTHER");
        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, manifest, SamplePrincipal("tenant-42"));

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be("cross-tenant invocation denied");
    }

    [Fact]
    public async Task ModelAllowlist_DeniedProvider_ReturnsReason()
    {
        await using var opa = new OpaContainer(FixturePath("model-allowlist.rego"));
        await opa.StartAsync();

        var engine = BuildEngine(opa.BaseUrl);
        var manifest = SampleManifest() with
        {
            Model = new ModelSpec(Provider: "google", Id: "gemini-pro"),
        };
        var decision = await engine.EvaluateAsync(PolicyOperation.Create, manifest, SamplePrincipal("tenant-42"));

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be("model provider not in allowlist");
    }

    [Fact]
    public async Task ModelAllowlist_AllowedProvider_Allows()
    {
        await using var opa = new OpaContainer(FixturePath("model-allowlist.rego"));
        await opa.StartAsync();

        var engine = BuildEngine(opa.BaseUrl);
        var manifest = SampleManifest() with
        {
            Model = new ModelSpec(Provider: "openai", Id: "gpt-4"),
        };
        var decision = await engine.EvaluateAsync(PolicyOperation.Create, manifest, SamplePrincipal("tenant-42"));

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task BudgetCap_ExceededMaxTokens_DeniesWithReason()
    {
        await using var opa = new OpaContainer(FixturePath("budget-cap.rego"));
        await opa.StartAsync();

        var engine = BuildEngine(opa.BaseUrl);
        var manifest = SampleManifest() with
        {
            Budget = new RunBudget(MaxPromptTokens: 500_000),
        };
        var decision = await engine.EvaluateAsync(PolicyOperation.Create, manifest, SamplePrincipal("tenant-42"));

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be("budget cap exceeded");
    }

    private static OpaPolicyEngine BuildEngine(Uri baseUrl)
    {
        var options = new OpaPolicyEngineOptions
        {
            BaseUrl = baseUrl,
            DataPath = "vais/agents/allow",
            DecisionCacheTtl = TimeSpan.Zero, // always round-trip through OPA for test determinism
            LogPolicyVersionOnStartup = false,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var client = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(10) };
        return new OpaPolicyEngine(
            client,
            new StubOptionsMonitor(options),
            TimeProvider.System,
            NullLogger<OpaPolicyEngine>.Instance);
    }

    private static AgentManifest SampleManifest() => new(
        Id: "chat",
        Version: "v1",
        Handler: new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: new[] { new ToolRef("weather") });

    private static AgentManifest SampleManifestWithLabel(string labelKey, string labelValue) =>
        SampleManifest() with
        {
            Labels = new Dictionary<string, string> { [labelKey] = labelValue },
        };

    private static AgentPrincipal SamplePrincipal(string tenantId) =>
        new(Id: "u1", TenantId: tenantId, Scopes: new[] { "agent:invoke" });

    private sealed class StubOptionsMonitor(OpaPolicyEngineOptions value) : IOptionsMonitor<OpaPolicyEngineOptions>
    {
        public OpaPolicyEngineOptions CurrentValue => value;
        public OpaPolicyEngineOptions Get(string? name) => value;
        public IDisposable OnChange(Action<OpaPolicyEngineOptions, string?> listener) => Noop.Instance;

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }
}
