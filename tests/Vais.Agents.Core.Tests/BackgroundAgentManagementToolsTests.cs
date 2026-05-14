// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class BackgroundAgentManagementToolsTests
{
    [Fact]
    public void Create_Returns_Three_Tools_With_Correct_Names()
    {
        var tracker = new FakeStaticTracker();
        var tools = BackgroundAgentManagementTools.Create(tracker);

        tools.Should().HaveCount(3);
        tools.Select(t => t.Name).Should().BeEquivalentTo(
        [
            "list_background_agents",
            "view_background_agent",
            "cancel_background_agent",
        ]);
    }

    [Fact]
    public void Create_Throws_When_Tracker_Is_Null()
    {
        var act = () => BackgroundAgentManagementTools.Create(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── list_background_agents ───────────────────────────────────────────────

    [Fact]
    public async Task ListTool_Returns_Records_For_Current_Parent_RunId()
    {
        var records = new[]
        {
            new BackgroundAgentRunRecord("h1", "parent-1", "worker", BackgroundAgentRunStatus.Completed,
                DateTimeOffset.UtcNow),
            new BackgroundAgentRunRecord("h2", "parent-1", "worker", BackgroundAgentRunStatus.Running,
                DateTimeOffset.UtcNow),
        };
        var tracker = new FakeStaticTracker(listResult: records);
        var tools = BackgroundAgentManagementTools.Create(tracker);
        var listTool = tools.Single(t => t.Name == "list_background_agents");

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { RunId = "parent-1" });

        var json = await listTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var arr = JsonDocument.Parse(json).RootElement;

        arr.ValueKind.Should().Be(JsonValueKind.Array);
        arr.GetArrayLength().Should().Be(2);
        tracker.LastListParentRunId.Should().Be("parent-1");
    }

    [Fact]
    public async Task ListTool_Uses_No_Run_When_RunId_Is_Null()
    {
        var tracker = new FakeStaticTracker();
        var tools = BackgroundAgentManagementTools.Create(tracker);
        var listTool = tools.Single(t => t.Name == "list_background_agents");

        // No AgentContext pushed — RunId will be null
        await listTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        tracker.LastListParentRunId.Should().Be("no-run");
    }

    // ── view_background_agent ────────────────────────────────────────────────

    [Fact]
    public async Task ViewTool_Returns_Record_As_Json()
    {
        var record = new BackgroundAgentRunRecord(
            "my-handle", "parent-1", "worker",
            BackgroundAgentRunStatus.Completed, DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow, Result: "42");
        var tracker = new FakeStaticTracker(getResult: record);
        var tools = BackgroundAgentManagementTools.Create(tracker);
        var viewTool = tools.Single(t => t.Name == "view_background_agent");

        var args = JsonDocument.Parse("""{"handle":"my-handle"}""").RootElement;
        var json = await viewTool.InvokeAsync(args, CancellationToken.None);

        var doc = JsonDocument.Parse(json).RootElement;
        doc.GetProperty("Handle").GetString().Should().Be("my-handle");
        tracker.LastGetHandle.Should().Be("my-handle");
    }

    [Fact]
    public async Task ViewTool_Returns_Error_When_Handle_Not_Found()
    {
        var tracker = new FakeStaticTracker(getResult: null);
        var tools = BackgroundAgentManagementTools.Create(tracker);
        var viewTool = tools.Single(t => t.Name == "view_background_agent");

        var args = JsonDocument.Parse("""{"handle":"missing"}""").RootElement;
        var json = await viewTool.InvokeAsync(args, CancellationToken.None);

        json.Should().Contain("error");
        json.Should().Contain("missing");
    }

    [Fact]
    public async Task ViewTool_Returns_Error_When_Handle_Is_Missing_From_Args()
    {
        var tracker = new FakeStaticTracker();
        var viewTool = BackgroundAgentManagementTools.Create(tracker).Single(t => t.Name == "view_background_agent");

        var json = await viewTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        json.Should().Contain("error");
    }

    // ── cancel_background_agent ──────────────────────────────────────────────

    [Fact]
    public async Task CancelTool_Returns_Cancelled_True_When_Tracker_Cancels()
    {
        var tracker = new FakeStaticTracker(cancelResult: true);
        var tools = BackgroundAgentManagementTools.Create(tracker);
        var cancelTool = tools.Single(t => t.Name == "cancel_background_agent");

        var args = JsonDocument.Parse("""{"handle":"h-run"}""").RootElement;
        var json = await cancelTool.InvokeAsync(args, CancellationToken.None);

        var doc = JsonDocument.Parse(json).RootElement;
        doc.GetProperty("handle").GetString().Should().Be("h-run");
        doc.GetProperty("cancelled").GetBoolean().Should().BeTrue();
        tracker.LastCancelHandle.Should().Be("h-run");
    }

    [Fact]
    public async Task CancelTool_Returns_Cancelled_False_When_Already_Completed()
    {
        var tracker = new FakeStaticTracker(cancelResult: false);
        var tools = BackgroundAgentManagementTools.Create(tracker);
        var cancelTool = tools.Single(t => t.Name == "cancel_background_agent");

        var args = JsonDocument.Parse("""{"handle":"done-run"}""").RootElement;
        var json = await cancelTool.InvokeAsync(args, CancellationToken.None);

        var doc = JsonDocument.Parse(json).RootElement;
        doc.GetProperty("cancelled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CancelTool_Returns_Error_When_Handle_Is_Missing()
    {
        var tracker = new FakeStaticTracker();
        var cancelTool = BackgroundAgentManagementTools.Create(tracker).Single(t => t.Name == "cancel_background_agent");

        var json = await cancelTool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        json.Should().Contain("error");
    }

    // ── Integration: cancel a completed run returns false ───────────────────

    [Fact]
    public async Task CancelTool_Returns_Cancelled_False_For_Already_Completed_Run()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("done"));
        var runtime = new InMemoryAgentRuntime(provider);
        var tracker = new InMemoryBackgroundAgentTracker(runtime);
        var tools = BackgroundAgentManagementTools.Create(tracker);

        var handle = await tracker.StartAsync(
            "parent-z", "worker", "sess-z", "fast task", new AgentContext());

        // Wait for completion.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var r = await tracker.GetAsync(handle);
            if (r?.Status == BackgroundAgentRunStatus.Completed) break;
            await Task.Delay(20);
        }

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { RunId = "parent-z" });
        var cancelTool = tools.Single(t => t.Name == "cancel_background_agent");
        var cancelArgs = JsonDocument.Parse($$$"""{"handle":"{{{handle}}}"}""").RootElement;
        var cancelJson = await cancelTool.InvokeAsync(cancelArgs, CancellationToken.None);

        var doc = JsonDocument.Parse(cancelJson).RootElement;
        doc.GetProperty("cancelled").GetBoolean().Should().BeFalse(
            "cancelling a completed run should return false");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class FakeStaticTracker : IBackgroundAgentTracker
    {
        private readonly IReadOnlyList<BackgroundAgentRunRecord>? _listResult;
        private readonly BackgroundAgentRunRecord? _getResult;
        private readonly bool _cancelResult;

        public string? LastListParentRunId { get; private set; }
        public string? LastGetHandle { get; private set; }
        public string? LastCancelHandle { get; private set; }

        public FakeStaticTracker(
            IReadOnlyList<BackgroundAgentRunRecord>? listResult = null,
            BackgroundAgentRunRecord? getResult = null,
            bool cancelResult = false)
        {
            _listResult = listResult;
            _getResult = getResult;
            _cancelResult = cancelResult;
        }

        public ValueTask<string> StartAsync(
            string parentRunId, string childAgentId, string childSessionId,
            string message, AgentContext childContext, CancellationToken ct = default)
            => ValueTask.FromResult(childSessionId);

        public ValueTask<BackgroundAgentRunRecord?> GetAsync(string handle, CancellationToken ct = default)
        {
            LastGetHandle = handle;
            return ValueTask.FromResult(_getResult);
        }

        public ValueTask<IReadOnlyList<BackgroundAgentRunRecord>> ListAsync(string parentRunId, CancellationToken ct = default)
        {
            LastListParentRunId = parentRunId;
            return ValueTask.FromResult(_listResult ?? (IReadOnlyList<BackgroundAgentRunRecord>)Array.Empty<BackgroundAgentRunRecord>());
        }

        public ValueTask<bool> CancelAsync(string handle, CancellationToken ct = default)
        {
            LastCancelHandle = handle;
            return ValueTask.FromResult(_cancelResult);
        }
    }
}
