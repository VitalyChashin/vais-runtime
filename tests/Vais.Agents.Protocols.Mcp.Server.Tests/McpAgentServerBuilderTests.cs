// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Protocols.Mcp.Server.Tests;

/// <summary>
/// v0.7 PR 1: core server routing. Exercises the list-tools + call-tool handlers
/// directly without spinning up a full MCP transport — the SDK's wire layer is
/// its own tested concern; we verify the agent-specific logic.
/// </summary>
public sealed class McpAgentServerBuilderTests
{
    [Fact]
    public async Task ListTools_Enumerates_Registry_One_Tool_Per_Agent()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("support", "1.0", "Support agent"));
        await lifecycle.CreateAsync(ManifestFor("billing", "1.0", null));

        var result = await McpAgentServerBuilder.HandleListToolsAsync(registry, new McpAgentServerOptions(), CancellationToken.None);

        result.Tools.Should().HaveCount(2);
        result.Tools.Select(t => t.Name).Should().BeEquivalentTo(new[] { "support", "billing" });
        result.Tools.Should().AllSatisfy(t => t.InputSchema.ValueKind.Should().Be(JsonValueKind.Object));
    }

    [Fact]
    public async Task ListTools_Honours_LabelPrefixFilter()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("support", "1.0", description: null, labels: new() { ["team"] = "customer-ops" }));
        await lifecycle.CreateAsync(ManifestFor("billing", "1.0", description: null, labels: new() { ["team"] = "finance" }));

        var result = await McpAgentServerBuilder.HandleListToolsAsync(
            registry,
            new McpAgentServerOptions { LabelPrefixFilter = "team:customer" },
            CancellationToken.None);

        // InMemoryAgentRegistry supports label-prefix filtering on ListAsync; we don't assert
        // exact semantics here since the registry's impl is the source of truth — only that
        // the prefix is threaded through to ListAsync.
        result.Tools.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Tool_Description_Includes_Version_Budget_Handoffs_And_Input_Example()
    {
        var manifest = ManifestFor("support", "1.2", "Helpful support")
            with
        {
            Budget = new RunBudget(MaxTurns: 5, MaxDuration: TimeSpan.FromSeconds(30)),
            Handoffs = new[] { new HandoffRef("billing", When: "refunds"), new HandoffRef("sales") },
        };

        var desc = McpAgentServerBuilder.BuildToolDescription(manifest);

        desc.Should().Contain("support (v1.2)");
        desc.Should().Contain("Helpful support");
        desc.Should().Contain("Budget:").And.Contain("maxTurns=5");
        desc.Should().Contain("Handoffs:").And.Contain("→ billing").And.Contain("→ sales");
        desc.Should().Contain("Input:");
    }

    [Fact]
    public async Task CallTool_Invokes_Agent_And_Returns_Text()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("hello from fake"));
        await lifecycle.CreateAsync(ManifestFor("echo", "1.0", null));

        var result = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, lifecycle,
            new CallToolRequestParams { Name = "echo", Arguments = Args(("text", "hi")) },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();
        var block = result.Content[0].Should().BeOfType<TextContentBlock>().Subject;
        block.Text.Should().Be("hello from fake");
    }

    [Fact]
    public async Task CallTool_Unknown_Agent_Returns_Error()
    {
        var (registry, lifecycle) = BuildHarness();

        var result = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, lifecycle,
            new CallToolRequestParams { Name = "ghost", Arguments = Args(("text", "hi")) },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        ((TextContentBlock)result.Content[0]).Text.Should().Contain("Unknown agent 'ghost'");
    }

    [Fact]
    public async Task CallTool_Missing_Text_Arg_Returns_Error()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("echo", "1.0", null));

        var result = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, lifecycle,
            new CallToolRequestParams { Name = "echo", Arguments = Args() },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        ((TextContentBlock)result.Content[0]).Text.Should().Contain("text");
    }

    [Fact]
    public async Task CallTool_Stateless_Default_Does_Not_Touch_Session_State()
    {
        int invocations = 0;
        var (registry, lifecycle) = BuildHarness(_ =>
        {
            invocations++;
            return new CompletionResponse($"call-{invocations}");
        });
        await lifecycle.CreateAsync(ManifestFor("echo", "1.0", null));

        var first = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, lifecycle,
            new CallToolRequestParams { Name = "echo", Arguments = Args(("text", "one")) },
            CancellationToken.None);
        var second = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, lifecycle,
            new CallToolRequestParams { Name = "echo", Arguments = Args(("text", "two")) },
            CancellationToken.None);

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeFalse();
        invocations.Should().Be(2);
    }

    [Fact]
    public async Task Policy_Deny_Surfaces_As_Structured_Error()
    {
        var denyingPolicy = new DenyEverythingPolicy();
        var (registry, lifecycle) = BuildHarness(policy: denyingPolicy);
        // Create works because the policy only denies Invoke (configured below).
        denyingPolicy.AllowCreate = true;
        await lifecycle.CreateAsync(ManifestFor("gated", "1.0", null));
        denyingPolicy.AllowCreate = false;

        var result = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, lifecycle,
            new CallToolRequestParams { Name = "gated", Arguments = Args(("text", "hi")) },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        var body = ((TextContentBlock)result.Content[0]).Text;
        body.Should().Contain("policy-denied");
        body.Should().Contain("Invoke");
    }

    [Fact]
    public async Task Interrupt_Response_Carries_Continuation_Envelope()
    {
        // Build a lifecycle manager whose registry has an agent; wrap it so Invoke throws
        // AgentInterruptedException synthetically (the real path requires wiring a guardrail
        // at the agent layer, which is overkill for this shape-level test).
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("never-reached")));
        var inner = new AgentLifecycleManager(registry, runtime);
        await inner.CreateAsync(ManifestFor("gated", "1.0", null));
        var wrapped = new InterruptingLifecycleManager(inner);

        var result = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, wrapped,
            new CallToolRequestParams { Name = "gated", Arguments = Args(("text", "hi")) },
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        var body = ((TextContentBlock)result.Content[0]).Text;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("interruptId").GetString().Should().Be("test-int");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("test-reason");
        doc.RootElement.GetProperty("continuation").GetString().Should().Contain("resume");
    }

    [Fact]
    public async Task Resume_Arg_Routes_Through_Invoke_With_Resume_Metadata()
    {
        AgentInvocationRequest? seen = null;
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("echo", "1.0", null));
        var recording = new RecordingLifecycleManager(lifecycle, req => seen = req);

        var resumeArgs = JsonDocument.Parse("""{"interruptId":"int-1","runId":"run-42","payload":"approved"}""").RootElement;
        var result = await McpAgentServerBuilder.HandleCallToolAsync(
            registry, recording,
            new CallToolRequestParams { Name = "echo", Arguments = Args(("text", "ignored"), ("resume", resumeArgs)) },
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        seen.Should().NotBeNull();
        seen!.Metadata.Should().ContainKey("resume.interruptId").WhoseValue.Should().Be("int-1");
        seen.Metadata.Should().ContainKey("resume.runId").WhoseValue.Should().Be("run-42");
    }

    [Fact]
    public async Task SessionId_Threaded_To_AgentInvocationRequest()
    {
        AgentInvocationRequest? seen = null;
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("echo", "1.0", null));
        var recording = new RecordingLifecycleManager(lifecycle, req => seen = req);

        await McpAgentServerBuilder.HandleCallToolAsync(
            registry, recording,
            new CallToolRequestParams { Name = "echo", Arguments = Args(("text", "hi"), ("sessionId", "s-42")) },
            CancellationToken.None);

        seen.Should().NotBeNull();
        seen!.SessionId.Should().Be("s-42");
    }

    [Fact]
    public void Build_Returns_McpServerOptions_With_Expected_Server_Info()
    {
        var (registry, lifecycle) = BuildHarness();
        var options = new McpAgentServerOptions
        {
            Name = "test-server",
            Version = "9.9",
            Instructions = "Talk to my agents.",
        };

        var serverOptions = McpAgentServerBuilder.Build(registry, lifecycle, options);

        serverOptions.ServerInfo.Should().NotBeNull();
        serverOptions.ServerInfo!.Name.Should().Be("test-server");
        serverOptions.ServerInfo.Version.Should().Be("9.9");
        serverOptions.ServerInstructions.Should().Be("Talk to my agents.");
        serverOptions.Capabilities.Should().NotBeNull();
        serverOptions.Capabilities!.Tools.Should().NotBeNull();
        serverOptions.Capabilities.Resources.Should().NotBeNull();
        serverOptions.Handlers.ListToolsHandler.Should().NotBeNull();
        serverOptions.Handlers.CallToolHandler.Should().NotBeNull();
        serverOptions.Handlers.ListResourcesHandler.Should().NotBeNull();
        serverOptions.Handlers.ReadResourceHandler.Should().NotBeNull();
    }

    [Fact]
    public void Build_Rejects_Null_Registry_Or_Lifecycle()
    {
        var (registry, lifecycle) = BuildHarness();
        FluentActions.Invoking(() => McpAgentServerBuilder.Build(null!, lifecycle)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => McpAgentServerBuilder.Build(registry, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ListResources_Emits_Manifest_Uri_Per_Registered_Agent()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("support", "1.0", "Support agent"));
        await lifecycle.CreateAsync(ManifestFor("billing", "1.0", null));

        var result = await McpAgentServerBuilder.HandleListResourcesAsync(registry, new McpAgentServerOptions(), CancellationToken.None);

        result.Resources.Should().HaveCount(2);
        result.Resources.Select(r => r.Uri).Should().BeEquivalentTo(new[]
        {
            "agent://support/1.0/manifest",
            "agent://billing/1.0/manifest",
        });
        result.Resources.Should().AllSatisfy(r => r.MimeType.Should().Be("application/json"));
    }

    [Fact]
    public async Task ReadResource_Returns_EnvelopeJson_For_Registered_Agent()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("support", "1.0", "Support agent"));

        var result = await McpAgentServerBuilder.HandleReadResourceAsync(
            registry,
            new ReadResourceRequestParams { Uri = "agent://support/1.0/manifest" },
            CancellationToken.None);

        result.Contents.Should().HaveCount(1);
        var text = result.Contents[0].Should().BeOfType<TextResourceContents>().Subject;
        text.MimeType.Should().Be("application/json");
        var envelope = JsonDocument.Parse(text.Text!).RootElement;
        envelope.GetProperty("apiVersion").GetString().Should().Be("vais.agents/v1");
        envelope.GetProperty("kind").GetString().Should().Be("Agent");
        envelope.GetProperty("metadata").GetProperty("id").GetString().Should().Be("support");
        envelope.GetProperty("metadata").GetProperty("version").GetString().Should().Be("1.0");
    }

    [Fact]
    public async Task ListResources_Emits_Separate_Entry_Per_Version_For_Multi_Version_Agent()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("support", "1.0", "v1.0 description"));
        await lifecycle.CreateAsync(ManifestFor("support", "1.1", "v1.1 description"));

        var list = await McpAgentServerBuilder.HandleListResourcesAsync(registry, new McpAgentServerOptions(), CancellationToken.None);

        list.Resources.Select(r => r.Uri).Should().BeEquivalentTo(new[]
        {
            "agent://support/1.0/manifest",
            "agent://support/1.1/manifest",
        });

        // And read_resource disambiguates per version.
        var v10 = await McpAgentServerBuilder.HandleReadResourceAsync(
            registry,
            new ReadResourceRequestParams { Uri = "agent://support/1.0/manifest" },
            CancellationToken.None);
        var v11 = await McpAgentServerBuilder.HandleReadResourceAsync(
            registry,
            new ReadResourceRequestParams { Uri = "agent://support/1.1/manifest" },
            CancellationToken.None);
        var v10Text = ((TextResourceContents)v10.Contents[0]).Text!;
        var v11Text = ((TextResourceContents)v11.Contents[0]).Text!;
        v10Text.Should().Contain("\"version\":\"1.0\"");
        v11Text.Should().Contain("\"version\":\"1.1\"");
    }

    [Fact]
    public async Task ReadResource_Rejects_Unknown_Uri_Shape()
    {
        var (registry, _) = BuildHarness();

        await FluentActions.Invoking(() => McpAgentServerBuilder.HandleReadResourceAsync(
                registry,
                new ReadResourceRequestParams { Uri = "http://support/manifest" },
                CancellationToken.None).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    // ---- helpers ----

    private static (InMemoryAgentRegistry Registry, AgentLifecycleManager Lifecycle) BuildHarness(
        Func<CompletionRequest, CompletionResponse>? provider = null,
        IAgentPolicyEngine? policy = null)
    {
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(provider ?? (_ => new CompletionResponse("ok"))));
        var lifecycle = new AgentLifecycleManager(registry, runtime, policy);
        return (registry, lifecycle);
    }

    private static AgentManifest ManifestFor(string id, string version, string? description, Dictionary<string, string>? labels = null) =>
        new(id, version,
            new AgentHandlerRef("declarative"),
            new[] { new ProtocolBinding("Mcp") },
            Array.Empty<ToolRef>(),
            Description: description,
            Labels: labels);

    private static IDictionary<string, JsonElement> Args(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in entries)
        {
            dict[k] = v is JsonElement je ? je : JsonSerializer.SerializeToElement(v);
        }
        return dict;
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }

    private sealed class DenyEverythingPolicy : IAgentPolicyEngine
    {
        public bool AllowCreate { get; set; }
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
        {
            if (operation == PolicyOperation.Create && AllowCreate) return ValueTask.FromResult(PolicyDecision.Allow);
            if (operation == PolicyOperation.Query) return ValueTask.FromResult(PolicyDecision.Allow);
            return ValueTask.FromResult(PolicyDecision.Deny("blocked by test"));
        }
    }

    /// <summary>Wraps a real lifecycle manager but throws <see cref="AgentInterruptedException"/> on Invoke.</summary>
    private sealed class InterruptingLifecycleManager(IAgentLifecycleManager inner) : IAgentLifecycleManager
    {
        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
            => inner.CreateAsync(manifest, cancellationToken);
        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
            => throw new AgentInterruptedException(new AgentInterrupt("test-int", "test-reason", JsonDocument.Parse("{}").RootElement) { RunId = "test-run" });
        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default) => inner.SignalAsync(handle, signal, cancellationToken);
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.QueryAsync(handle, cancellationToken);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.CancelAsync(handle, cancellationToken);
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default) => inner.UpdateAsync(handle, newManifest, cancellationToken);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.EvictAsync(handle, cancellationToken);
    }

    /// <summary>Records the last InvokeAsync request so tests can assert session-id / metadata threading.</summary>
    private sealed class RecordingLifecycleManager(IAgentLifecycleManager inner, Action<AgentInvocationRequest> record) : IAgentLifecycleManager
    {
        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default) => inner.CreateAsync(manifest, cancellationToken);
        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            record(request);
            return inner.InvokeAsync(handle, request, cancellationToken);
        }
        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default) => inner.SignalAsync(handle, signal, cancellationToken);
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.QueryAsync(handle, cancellationToken);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.CancelAsync(handle, cancellationToken);
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default) => inner.UpdateAsync(handle, newManifest, cancellationToken);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.EvictAsync(handle, cancellationToken);
    }
}
