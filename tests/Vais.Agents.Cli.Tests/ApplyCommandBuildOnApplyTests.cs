// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Vais.Agents.Control.Http;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// Unit tests for the build-on-apply logic in <see cref="ApplyCommand.ApplyContainerPluginAsync"/>.
/// Uses injectable static hooks (DockerRun, DockerImageExists) so no real Docker daemon is needed.
/// </summary>
public sealed class ApplyCommandBuildOnApplyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;
    private readonly Func<string, CancellationToken, Task<int>> _savedDockerRun;
    private readonly Func<string, CancellationToken, Task<bool>> _savedDockerImageExists;

    private readonly List<string> _dockerCalls = new();
    private bool _imageExists;

    public ApplyCommandBuildOnApplyTests()
    {
        _savedDockerRun = ApplyCommand.DockerRun;
        _savedDockerImageExists = ApplyCommand.DockerImageExists;

        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "plugin.yaml");

        ApplyCommand.DockerRun = (args, _) => { _dockerCalls.Add(args); return Task.FromResult(0); };
        ApplyCommand.DockerImageExists = (_, _) => Task.FromResult(_imageExists);
    }

    public void Dispose()
    {
        ApplyCommand.DockerRun = _savedDockerRun;
        ApplyCommand.DockerImageExists = _savedDockerImageExists;
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ContainerPluginManifest ManifestWithBuild(bool push = false, IReadOnlyDictionary<string, string>? args = null)
        => new("my-plugin", "1.0")
        {
            Spec = new ContainerPluginSpec
            {
                Image = "my-registry/my-plugin:1.0",
                Build = new ContainerPluginBuildSpec { Context = "./", Push = push, Args = args },
            },
        };

    private static ContainerPluginManifest ManifestNoBuild()
        => new("my-plugin", "1.0")
        {
            Spec = new ContainerPluginSpec { Image = "my-registry/my-plugin:1.0" },
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImageNotCached_BuildRunsThenManifestPosted()
    {
        _imageExists = false;
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeTrue();
        _dockerCalls.Should().HaveCount(1);
        _dockerCalls[0].Should().StartWith("build -t my-registry/my-plugin:1.0");
        client.CreateCalls.Should().HaveCount(1);
        client.UpdateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ImageCached_BuildSkipped_ManifestPosted()
    {
        _imageExists = true;
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task NoBuildFlag_BuildSkippedRegardlessOfCache()
    {
        _imageExists = false;
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(), "key", _manifestPath, noBuild: true, CancellationToken.None);

        result.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task PushTrue_BuildThenPushThenPost()
    {
        _imageExists = false;
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(push: true), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeTrue();
        _dockerCalls.Should().HaveCount(2);
        _dockerCalls[0].Should().StartWith("build ");
        _dockerCalls[1].Should().Be("push my-registry/my-plugin:1.0");
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task NoBuildSpec_NoDockerCalls_ManifestPosted()
    {
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestNoBuild(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task DockerBuildFails_ReturnsFalse_ManifestNotPosted()
    {
        _imageExists = false;
        ApplyCommand.DockerRun = (args, _) => { _dockerCalls.Add(args); return Task.FromResult(1); };
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeFalse();
        client.CreateCalls.Should().BeEmpty();
        client.UpdateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DockerPushFails_ReturnsFalse_ManifestNotPosted()
    {
        _imageExists = false;
        var callCount = 0;
        ApplyCommand.DockerRun = (args, _) =>
        {
            _dockerCalls.Add(args);
            callCount++;
            return Task.FromResult(callCount == 1 ? 0 : 1); // build OK, push fails
        };
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(push: true), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeFalse();
        _dockerCalls.Should().HaveCount(2);
        client.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Conflict_CreateThrows409_FallsBackToUpdate()
    {
        _imageExists = true;
        var client = new FakeClient { ThrowConflictOnCreate = true };

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        result.Should().BeTrue();
        client.CreateCalls.Should().HaveCount(1);
        client.UpdateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task BuildArgs_PassedToDockerBuildCommand()
    {
        _imageExists = false;
        var client = new FakeClient();
        var manifest = ManifestWithBuild(args: new Dictionary<string, string> { ["API_KEY"] = "secret" });

        await ApplyCommand.ApplyContainerPluginAsync(
            client, manifest, "key", _manifestPath, noBuild: false, CancellationToken.None);

        _dockerCalls.Should().HaveCount(1);
        _dockerCalls[0].Should().Contain("--build-arg API_KEY=secret");
    }

    [Fact]
    public async Task StdinManifest_RelativeContext_UsesCurrentDirectory()
    {
        _imageExists = false;
        var client = new FakeClient();

        var result = await ApplyCommand.ApplyContainerPluginAsync(
            client, ManifestWithBuild(), "key", manifestFilePath: "-", noBuild: false, CancellationToken.None);

        result.Should().BeTrue();
        _dockerCalls.Should().HaveCount(1);
        // Context should resolve to CWD — just confirm a build call ran without crashing
        _dockerCalls[0].Should().StartWith("build -t");
    }

    // ── Fake control plane client ─────────────────────────────────────────────

    private sealed class FakeClient : IAgentControlPlaneClient
    {
        public bool ThrowConflictOnCreate { get; set; }
        public List<ContainerPluginManifest> CreateCalls { get; } = new();
        public List<(string Id, string? Version)> UpdateCalls { get; } = new();

        // Container plugin overrides (DIM defaults throw NotSupportedException).
        public Task<ContainerPluginHandle> CreateContainerPluginAsync(
            ContainerPluginManifest manifest, CancellationToken cancellationToken = default)
        {
            CreateCalls.Add(manifest);
            if (ThrowConflictOnCreate)
                throw new AgentControlPlaneException(409, null, "Conflict", null);
            return Task.FromResult(new ContainerPluginHandle(manifest.Id, manifest.Version));
        }

        public Task<ContainerPluginHandle> UpdateContainerPluginAsync(
            string id, ContainerPluginManifest manifest, string? version = null, CancellationToken cancellationToken = default)
        {
            UpdateCalls.Add((id, version));
            return Task.FromResult(new ContainerPluginHandle(id, version ?? manifest.Version));
        }

        // Required abstract members — stubs (not exercised by these tests).
        public Task<AgentHandle> CreateAsync(AgentManifest m, CancellationToken ct = default)
            => Task.FromResult(new AgentHandle(m.Id, m.Version));
        public Task<IReadOnlyList<AgentManifest>> ListAsync(string? l = null, int? lim = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentManifest>>([]);
        public Task<AgentQueryResponse?> QueryAsync(string id, string? v = null, CancellationToken ct = default)
            => Task.FromResult<AgentQueryResponse?>(null);
        public Task<AgentHandle> UpdateAsync(string id, AgentManifest m, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new AgentHandle(id, v ?? m.Version));
        public Task<AgentHandle> UpdateAsync(string id, AgentManifest m, string? v, string? key, CancellationToken ct)
            => UpdateAsync(id, m, v, ct);
        public Task CancelAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelAsync(string id, string? v, string? key, CancellationToken ct) => Task.CompletedTask;
        public Task EvictAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task EvictAsync(string id, string? v, string? key, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentInvocationResult> InvokeAsync(string id, AgentInvocationRequest req, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new AgentInvocationResult(""));
        public Task SignalAsync(string id, AgentSignal sig, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest m, CancellationToken ct = default)
            => Task.FromResult(new AgentGraphHandle(m.Id, m.Version));
        public Task<AgentGraphListResponse> ListGraphsAsync(string? l = null, int? lim = null, string? cursor = null, CancellationToken ct = default)
            => Task.FromResult(new AgentGraphListResponse([]));
        public Task<AgentGraphQueryResponse?> QueryGraphAsync(string id, string? v = null, CancellationToken ct = default)
            => Task.FromResult<AgentGraphQueryResponse?>(null);
        public Task<AgentGraphHandle> UpdateGraphAsync(string id, AgentGraphManifest m, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new AgentGraphHandle(id, v ?? m.Version));
        public Task EvictGraphAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<GraphInvocationResult> InvokeGraphAsync(string id, GraphInvocationRequest req, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new GraphInvocationResult(req.RunId ?? "r1", new Dictionary<string, System.Text.Json.JsonElement>(), IsComplete: true));
        public Task<GraphInvocationResult> ResumeGraphAsync(string id, string runId, GraphResumeRequest req, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new GraphInvocationResult(runId, new Dictionary<string, System.Text.Json.JsonElement>(), IsComplete: true));
        public Task CancelGraphRunAsync(string id, string runId, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<LlmGatewayConfigHandle> CreateLlmGatewayConfigAsync(LlmGatewayConfigManifest m, CancellationToken ct = default)
            => Task.FromResult(new LlmGatewayConfigHandle(m.Id, m.Version));
        public Task<LlmGatewayConfigHandle> UpdateLlmGatewayConfigAsync(string id, LlmGatewayConfigManifest m, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new LlmGatewayConfigHandle(id, m.Version));
        public Task<LlmGatewayConfigListResponse> ListLlmGatewayConfigsAsync(string? l = null, int? lim = null, string? cursor = null, CancellationToken ct = default)
            => Task.FromResult(new LlmGatewayConfigListResponse([]));
        public Task<LlmGatewayConfigQueryResponse?> QueryLlmGatewayConfigAsync(string id, string? v = null, CancellationToken ct = default)
            => Task.FromResult<LlmGatewayConfigQueryResponse?>(null);
        public Task EvictLlmGatewayConfigAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<McpGatewayConfigHandle> CreateMcpGatewayConfigAsync(McpGatewayConfigManifest m, CancellationToken ct = default)
            => Task.FromResult(new McpGatewayConfigHandle(m.Id, m.Version));
        public Task<McpGatewayConfigHandle> UpdateMcpGatewayConfigAsync(string id, McpGatewayConfigManifest m, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new McpGatewayConfigHandle(id, m.Version));
        public Task<McpGatewayConfigListResponse> ListMcpGatewayConfigsAsync(string? l = null, int? lim = null, string? cursor = null, CancellationToken ct = default)
            => Task.FromResult(new McpGatewayConfigListResponse([]));
        public Task<McpGatewayConfigQueryResponse?> QueryMcpGatewayConfigAsync(string id, string? v = null, CancellationToken ct = default)
            => Task.FromResult<McpGatewayConfigQueryResponse?>(null);
        public Task EvictMcpGatewayConfigAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest m, CancellationToken ct = default)
            => Task.FromResult(new McpServerHandle(m.Id, m.Version));
        public Task<McpServerHandle> UpdateMcpServerAsync(string id, McpServerManifest m, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new McpServerHandle(id, m.Version));
        public Task<McpServerListResponse> ListMcpServersAsync(string? l = null, int? lim = null, string? cursor = null, CancellationToken ct = default)
            => Task.FromResult(new McpServerListResponse([]));
        public Task<McpServerQueryResponse?> QueryMcpServerAsync(string id, string? v = null, CancellationToken ct = default)
            => Task.FromResult<McpServerQueryResponse?>(null);
        public Task EvictMcpServerAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
