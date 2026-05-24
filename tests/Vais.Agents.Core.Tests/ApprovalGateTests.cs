// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// NB-6 / NB-7: the approval store + gate. A high-risk apply is held (throws with a
/// request id) until an operator approves the exact manifest; a tampered manifest stays
/// held; a rejected request stays blocked; non-high-risk kinds pass straight through.
/// </summary>
public sealed class ApprovalGateTests
{
    private const string Plugin = "ContainerPlugin";

    private static (ApprovalGate gate, InMemoryApprovalStore store) NewGate()
    {
        var store = new InMemoryApprovalStore();
        return (new ApprovalGate(store), store);
    }

    private static async Task<ApprovalRequiredException> Held(
        ApprovalGate gate, string kind, string name, string canonical, string by = "alice")
        => (await gate.Invoking(g => g.EnsureApprovedAsync(kind, name, canonical, by).AsTask())
                .Should().ThrowAsync<ApprovalRequiredException>()).Which;

    private static Task ShouldProceed(ApprovalGate gate, string kind, string name, string canonical, string by = "alice")
        => gate.Invoking(g => g.EnsureApprovedAsync(kind, name, canonical, by).AsTask()).Should().NotThrowAsync();

    [Fact]
    public async Task HighRisk_Apply_Holds_With_RequestId_And_Mutates_Nothing()
    {
        var (gate, store) = NewGate();

        var ex = await Held(gate, Plugin, "p1", "manifest-v1");

        ex.RequestId.Should().NotBeNullOrEmpty();
        ex.Kind.Should().Be(Plugin);
        var pending = await store.ListAsync(ApprovalStatus.Pending);
        pending.Should().ContainSingle().Which.RequestId.Should().Be(ex.RequestId);
    }

    [Fact]
    public async Task ReApply_Of_Same_Held_Manifest_Is_Idempotent()
    {
        var (gate, _) = NewGate();

        var first = (await Held(gate, Plugin, "p1", "manifest-v1")).RequestId;
        var second = (await Held(gate, Plugin, "p1", "manifest-v1")).RequestId;

        second.Should().Be(first, "the same held manifest reuses its pending request");
    }

    [Fact]
    public async Task Approve_Then_ReApply_Proceeds()
    {
        var (gate, store) = NewGate();
        var requestId = (await Held(gate, Plugin, "p1", "manifest-v1")).RequestId;

        var decided = await store.DecideAsync(requestId, approve: true, "operator");
        decided!.Status.Should().Be(ApprovalStatus.Approved);

        await ShouldProceed(gate, Plugin, "p1", "manifest-v1");
    }

    [Fact]
    public async Task Tampered_Manifest_Stays_Held_After_Approval()
    {
        var (gate, store) = NewGate();
        var requestId = (await Held(gate, Plugin, "p1", "manifest-v1")).RequestId;
        await store.DecideAsync(requestId, approve: true, "operator");

        // A different manifest re-hashes → no matching approval → held again with a NEW request.
        var tampered = await Held(gate, Plugin, "p1", "manifest-v2-tampered");
        tampered.RequestId.Should().NotBe(requestId);
    }

    [Fact]
    public async Task Rejected_Request_Stays_Blocked()
    {
        var (gate, store) = NewGate();
        var requestId = (await Held(gate, Plugin, "p1", "manifest-v1")).RequestId;

        await store.DecideAsync(requestId, approve: false, "operator");

        await Held(gate, Plugin, "p1", "manifest-v1"); // still blocked — rejection does not authorize
    }

    [Fact]
    public async Task NonHighRisk_Kind_Passes_Through()
        => await ShouldProceed(new ApprovalGate(new InMemoryApprovalStore()), "Agent", "a1", "manifest-v1");

    [Fact]
    public async Task Store_Decide_Unknown_Returns_Null()
        => (await new InMemoryApprovalStore().DecideAsync("nope", approve: true, "operator")).Should().BeNull();

    [Fact]
    public async Task Store_List_Filters_By_Status()
    {
        var store = new InMemoryApprovalStore();
        var a = await store.CreatePendingAsync(Plugin, "p1", "h1", "alice");
        await store.CreatePendingAsync(Plugin, "p2", "h2", "alice");
        await store.DecideAsync(a.RequestId, approve: true, "operator");

        (await store.ListAsync(ApprovalStatus.Pending)).Should().ContainSingle();
        (await store.ListAsync(ApprovalStatus.Approved)).Should().ContainSingle();
        (await store.ListAsync()).Should().HaveCount(2);
    }
}
