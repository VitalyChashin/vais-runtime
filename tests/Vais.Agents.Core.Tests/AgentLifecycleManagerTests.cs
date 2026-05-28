// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.6 PR 1b: <see cref="AgentLifecycleManager"/> routes the seven verbs through
/// policy + audit + registry + runtime. Runtime-neutral — works here over
/// <see cref="InMemoryAgentRuntime"/>; Orleans consumers wire the same manager
/// over <c>OrleansAgentRuntime</c> identically.
/// </summary>
public sealed class AgentLifecycleManagerTests
{
    private static readonly AgentManifest SupportManifest = new(
        "support", "1.0",
        new AgentHandlerRef("Support"),
        new[] { new ProtocolBinding("Http") },
        Array.Empty<ToolRef>());

    [Fact]
    public async Task Create_Registers_Manifest_And_Returns_Handle()
    {
        var (manager, _, audit) = BuildManager();

        var handle = await manager.CreateAsync(SupportManifest);

        handle.AgentId.Should().Be("support");
        handle.Version.Should().Be("1.0");
        audit.Entries.Should().ContainSingle();
        audit.Entries[0].Operation.Should().Be(PolicyOperation.Create);
        audit.Entries[0].Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Query_Unknown_Handle_Returns_Unknown()
    {
        var (manager, _, _) = BuildManager();
        var status = await manager.QueryAsync(new AgentHandle("ghost", "1.0"));
        status.Should().Be(AgentStatus.Unknown);
    }

    [Fact]
    public async Task Query_Known_Idle_Handle_Returns_Idle()
    {
        var (manager, _, _) = BuildManager();
        var handle = await manager.CreateAsync(SupportManifest);

        var status = await manager.QueryAsync(handle);
        status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task Invoke_Happy_Path_Returns_AgentReply()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("hello back"));
        var (manager, _, audit) = BuildManager(provider);
        var handle = await manager.CreateAsync(SupportManifest);

        var result = await manager.InvokeAsync(handle, new AgentInvocationRequest("hi"));

        result.Text.Should().Be("hello back");
        audit.Entries.Select(e => e.Operation).Should().Equal(PolicyOperation.Create, PolicyOperation.Invoke);
    }

    [Fact]
    public async Task Invoke_Unknown_Handle_Throws_AgentHandleNotFound()
    {
        var (manager, registry, _) = BuildManager();
        registry.Register(SupportManifest);

        var ghostHandle = new AgentHandle("support", "9.9");
        var ex = await FluentActions.Invoking(async () => await manager.InvokeAsync(ghostHandle, new AgentInvocationRequest("hi")))
            .Should().ThrowAsync<AgentHandleNotFoundException>();
        ex.Which.AgentId.Should().Be("support");
        ex.Which.Version.Should().Be("9.9");
    }

    [Fact]
    public async Task Invoke_RegistrySeededOutOfBand_LazyHydrates_State_And_Succeeds()
    {
        // Models silo restart: the durable registry recovers the manifest (here we register
        // it directly without going through manager.CreateAsync) but the in-process _state
        // counter dict is empty. Pre-fix InvokeAsync threw "Unknown agent handle"; post-fix
        // it lazy-hydrates a zero AgentState and returns the agent's reply.
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("hello back"));
        var (manager, registry, audit) = BuildManager(provider);
        registry.Register(SupportManifest);

        var handle = new AgentHandle("support", "1.0");
        var result = await manager.InvokeAsync(handle, new AgentInvocationRequest("hi"));

        result.Text.Should().Be("hello back");
        audit.Entries.Select(e => e.Operation).Should().Equal(PolicyOperation.Invoke);
        audit.Entries[0].ErrorType.Should().BeNull("lazy-hydrate succeeded; no errorType should be audited");
    }

    [Fact]
    public async Task Invoke_AfterLazyHydrate_NewVersionViaCreateAsync_StillSucceeds()
    {
        // Regression against GetOrAdd swallowing later explicit registrations: once we've
        // lazy-hydrated v1 from the registry, registering v2 via CreateAsync must still
        // work, and both versions must be invokable independently.
        var provider = new FakeCompletionProvider(req =>
            new CompletionResponse(req.History.LastOrDefault()?.Text ?? "(none)"));
        var (manager, registry, _) = BuildManager(provider);
        registry.Register(SupportManifest);

        // First invoke triggers lazy-hydrate for v1.
        var v1 = await manager.InvokeAsync(new AgentHandle("support", "1.0"), new AgentInvocationRequest("first"));
        v1.Text.Should().Be("first");

        // Register a new version through the manager's normal CreateAsync path.
        var v2Manifest = SupportManifest with { Version = "2.0" };
        var v2Handle = await manager.CreateAsync(v2Manifest);
        v2Handle.Version.Should().Be("2.0");

        // Both versions invokable.
        var v2 = await manager.InvokeAsync(v2Handle, new AgentInvocationRequest("second"));
        v2.Text.Should().Be("second");
        var v1Again = await manager.InvokeAsync(new AgentHandle("support", "1.0"), new AgentInvocationRequest("third"));
        v1Again.Text.Should().Be("third");
    }

    [Fact]
    public async Task Policy_Deny_Short_Circuits_With_Typed_Exception_And_Audit()
    {
        var policy = new ConditionalPolicy(op => op == PolicyOperation.Create
            ? PolicyDecision.Deny("test-deny")
            : PolicyDecision.Allow);
        var (manager, _, audit) = BuildManager(policy: policy);

        var ex = await FluentActions.Invoking(async () => await manager.CreateAsync(SupportManifest))
            .Should().ThrowAsync<AgentPolicyDeniedException>();
        ex.Which.Operation.Should().Be(PolicyOperation.Create);
        ex.Which.Reason.Should().Be("test-deny");

        audit.Entries.Should().ContainSingle();
        audit.Entries[0].Allowed.Should().BeFalse();
        audit.Entries[0].DenyReason.Should().Be("test-deny");
    }

    [Fact]
    public async Task Policy_Sees_Every_Verb()
    {
        var seen = new List<PolicyOperation>();
        var policy = new ConditionalPolicy(op =>
        {
            seen.Add(op);
            return PolicyDecision.Allow;
        });
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var (manager, _, _) = BuildManager(provider, policy);

        var handle = await manager.CreateAsync(SupportManifest);
        await manager.InvokeAsync(handle, new AgentInvocationRequest("hi"));
        await manager.SignalAsync(handle, new AgentSignal("ping", System.Text.Json.JsonDocument.Parse("{}").RootElement));
        await manager.QueryAsync(handle);
        await manager.CancelAsync(handle);
        await manager.UpdateAsync(handle, SupportManifest with { Version = "1.1" });
        await manager.EvictAsync(handle);

        seen.Should().Contain(new[]
        {
            PolicyOperation.Create, PolicyOperation.Invoke, PolicyOperation.Signal,
            PolicyOperation.Query, PolicyOperation.Cancel, PolicyOperation.Update, PolicyOperation.Evict,
        });
    }

    [Fact]
    public async Task Update_Registers_New_Version()
    {
        var (manager, registry, _) = BuildManager();
        var v1 = await manager.CreateAsync(SupportManifest);

        var v11 = await manager.UpdateAsync(v1, SupportManifest with { Version = "1.1" });

        v11.Version.Should().Be("1.1");
        (await registry.GetAsync("support", "1.0")).Should().NotBeNull();
        (await registry.GetAsync("support", "1.1")).Should().NotBeNull();
    }

    [Fact]
    public async Task Update_Rejects_Mismatched_Id()
    {
        var (manager, _, _) = BuildManager();
        var handle = await manager.CreateAsync(SupportManifest);

        var differentId = SupportManifest with { Id = "other" };
        await FluentActions.Invoking(async () => await manager.UpdateAsync(handle, differentId))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Evict_Removes_Registration_And_Runtime_Proxy()
    {
        var (manager, registry, _) = BuildManager();
        var handle = await manager.CreateAsync(SupportManifest);

        await manager.EvictAsync(handle);

        (await registry.GetAsync("support", "1.0")).Should().BeNull();
        (await manager.QueryAsync(handle)).Should().Be(AgentStatus.Unknown);
    }

    [Fact]
    public async Task Signal_Is_A_No_Op_But_Audited()
    {
        var (manager, _, audit) = BuildManager();
        var handle = await manager.CreateAsync(SupportManifest);

        await manager.SignalAsync(handle, new AgentSignal("resume", System.Text.Json.JsonDocument.Parse("""{"ok":true}""").RootElement));

        audit.Entries.Select(e => e.Operation).Should().Contain(PolicyOperation.Signal);
    }

    [Fact]
    public async Task Null_Policy_And_Audit_Preserve_Default_Behaviour()
    {
        // Verify the manager works end-to-end with the NullPolicy + NullAudit defaults.
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
        var manager = new AgentLifecycleManager(registry, runtime);

        var handle = await manager.CreateAsync(SupportManifest);
        var result = await manager.InvokeAsync(handle, new AgentInvocationRequest("hi"));

        result.Text.Should().Be("ok");
    }

    [Fact]
    public async Task Principal_Synthesized_From_Ambient_Context()
    {
        var accessor = new AsyncLocalAgentContextAccessor();
        using var scope = accessor.Push(new AgentContext(UserId: "alice", TenantId: "acme"));

        var recordingPolicy = new RecordingPolicy();
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
        var manager = new AgentLifecycleManager(registry, runtime, recordingPolicy, audit: null, contextAccessor: accessor);

        await manager.CreateAsync(SupportManifest);

        recordingPolicy.SeenPrincipals.Should().ContainSingle();
        recordingPolicy.SeenPrincipals[0]!.Id.Should().Be("alice");
        recordingPolicy.SeenPrincipals[0]!.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task Principal_Carries_Scopes_From_Ambient_Context()
    {
        // NB-2: the policy seam must see the caller's JWT scopes — SynthesizePrincipal
        // now propagates AgentContext.Scopes instead of hard-coding null.
        var accessor = new AsyncLocalAgentContextAccessor();
        using var scope = accessor.Push(
            new AgentContext(UserId: "alice", TenantId: "acme")
            {
                Scopes = new[] { "vais.author:Agent", "vais.read" },
            });

        var recordingPolicy = new RecordingPolicy();
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
        var manager = new AgentLifecycleManager(registry, runtime, recordingPolicy, audit: null, contextAccessor: accessor);

        await manager.CreateAsync(SupportManifest);

        recordingPolicy.SeenPrincipals.Should().ContainSingle();
        recordingPolicy.SeenPrincipals[0]!.Scopes.Should().BeEquivalentTo("vais.author:Agent", "vais.read");
    }

    [Fact]
    public async Task Principal_Scopes_Null_When_Context_Has_None()
    {
        // The no-JWT localhost path must keep Scopes null (allow-by-default preserved).
        var accessor = new AsyncLocalAgentContextAccessor();
        using var scope = accessor.Push(new AgentContext(UserId: "bob"));

        var recordingPolicy = new RecordingPolicy();
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
        var manager = new AgentLifecycleManager(registry, runtime, recordingPolicy, audit: null, contextAccessor: accessor);

        await manager.CreateAsync(SupportManifest);

        recordingPolicy.SeenPrincipals.Should().ContainSingle();
        recordingPolicy.SeenPrincipals[0]!.Scopes.Should().BeNull();
    }

    // ---- helpers ----

    private static (AgentLifecycleManager manager, InMemoryAgentRegistry registry, RecordingAuditLog audit) BuildManager(
        ICompletionProvider? provider = null,
        IAgentPolicyEngine? policy = null)
    {
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(provider ?? new FakeCompletionProvider(_ => new CompletionResponse("ok")));
        var audit = new RecordingAuditLog();
        var manager = new AgentLifecycleManager(registry, runtime, policy, audit);
        return (manager, registry, audit);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditLogEntry> Entries { get; } = new();
        public ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConditionalPolicy(Func<PolicyOperation, PolicyDecision> decide) : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(decide(operation));
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
}
