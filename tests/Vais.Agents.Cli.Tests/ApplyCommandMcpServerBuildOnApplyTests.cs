// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Vais.Agents.Control.Http;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// CMS-5: build-on-apply for <c>kind: McpServer</c> with <c>transport: containerStdio</c>
/// and <c>spec.container.build</c>. Mirror of <see cref="ApplyCommandBuildOnApplyTests"/>
/// (ContainerPlugin) for the MCP server variant.
/// </summary>
[Collection(ApplyCommandStaticsCollection.Name)]
public sealed class ApplyCommandMcpServerBuildOnApplyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _manifestPath;
    private readonly Func<string, CancellationToken, Task<int>> _savedDockerRun;
    private readonly Func<string, CancellationToken, Task<bool>> _savedDockerImageExists;

    private readonly List<string> _dockerCalls = new();
    private bool _imageExists;

    public ApplyCommandMcpServerBuildOnApplyTests()
    {
        _savedDockerRun = ApplyCommand.DockerRun;
        _savedDockerImageExists = ApplyCommand.DockerImageExists;

        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _manifestPath = Path.Combine(_tempDir, "mcp-fetch.yaml");

        ApplyCommand.DockerRun = (args, _) => { _dockerCalls.Add(args); return Task.FromResult(0); };
        ApplyCommand.DockerImageExists = (_, _) => Task.FromResult(_imageExists);
    }

    public void Dispose()
    {
        ApplyCommand.DockerRun = _savedDockerRun;
        ApplyCommand.DockerImageExists = _savedDockerImageExists;
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static McpServerManifest ManifestWithBuild(string? image = null, bool push = false)
        => new("mcp-fetch", "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec
            {
                Image = image,
                Build = new ContainerMcpBuildSpec { Context = "./", Push = push },
            },
        };

    private static McpServerManifest ManifestNoBuild()
        => new("mcp-fetch", "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec { Image = "my-registry/mcp-fetch:1.0" },
        };

    private static McpServerManifest ManifestStreamableHttp()
        => new("mcp-http", "1.0") { Transport = "streamableHttp", Url = "http://localhost:9000/mcp" };

    [Fact]
    public async Task BuildWithExplicitImage_RunsBuild_PostsManifest()
    {
        _imageExists = false;
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild("my-registry/mcp-fetch:1.0"), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().HaveCount(1);
        _dockerCalls[0].Should().StartWith("build -t my-registry/mcp-fetch:1.0");
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task BuildWithoutExplicitImage_DerivesTagFromIdAndVersion()
    {
        _imageExists = false;
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild(image: null), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().HaveCount(1);
        _dockerCalls[0].Should().StartWith("build -t vais-mcp-mcp-fetch:1.0");
        client.CreateCalls.Should().HaveCount(1);
        // Patched manifest carries the resolved image
        client.CreateCalls[0].Container!.Image.Should().Be("vais-mcp-mcp-fetch:1.0");
    }

    [Fact]
    public async Task ImageCached_BuildSkipped()
    {
        _imageExists = true;
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild("foo:1.0"), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task NoBuildFlag_BuildSkipped()
    {
        _imageExists = false;
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild("foo:1.0"), "key", _manifestPath, noBuild: true, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task PushTrue_BuildThenPush()
    {
        _imageExists = false;
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild("foo:1.0", push: true), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().HaveCount(2);
        _dockerCalls[0].Should().StartWith("build ");
        _dockerCalls[1].Should().Be("push foo:1.0");
    }

    [Fact]
    public async Task BuildFailure_ReturnsFalse_ManifestNotPosted()
    {
        _imageExists = false;
        ApplyCommand.DockerRun = (args, _) => { _dockerCalls.Add(args); return Task.FromResult(1); };
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild("foo:1.0"), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeFalse();
        client.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task NoContainerBlock_NoBuild_StreamableHttp_PostsAsIs()
    {
        // Regression: existing transports never trigger build logic.
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestStreamableHttp(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task ContainerWithoutBuild_PostsAsIs()
    {
        var client = new FakeMcpServerClient();

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestNoBuild(), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        _dockerCalls.Should().BeEmpty();
        client.CreateCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Conflict_FallsBackToUpdate()
    {
        _imageExists = true;
        var client = new FakeMcpServerClient { ThrowConflictOnCreate = true };

        var ok = await ApplyCommand.ApplyMcpServerAsync(
            client, ManifestWithBuild("foo:1.0"), "key", _manifestPath, noBuild: false, CancellationToken.None);

        ok.Should().BeTrue();
        client.CreateCalls.Should().HaveCount(1);
        client.UpdateCalls.Should().HaveCount(1);
    }

    // ── Fake client (minimal McpServer-only surface; default-method overloads handle the rest) ────────

    private sealed class FakeMcpServerClient : IAgentControlPlaneClient
    {
        public bool ThrowConflictOnCreate { get; set; }
        public List<McpServerManifest> CreateCalls { get; } = new();
        public List<(string Id, string? Version)> UpdateCalls { get; } = new();

        public Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest m, CancellationToken ct = default)
        {
            CreateCalls.Add(m);
            if (ThrowConflictOnCreate)
                throw new AgentControlPlaneException(409, null, "Conflict", null);
            return Task.FromResult(new McpServerHandle(m.Id, m.Version));
        }

        public Task<McpServerHandle> UpdateMcpServerAsync(
            string id, McpServerManifest m, string? v = null, CancellationToken ct = default)
        {
            UpdateCalls.Add((id, v));
            return Task.FromResult(new McpServerHandle(id, v ?? m.Version));
        }

        // The rest of the interface — stubs that aren't exercised by these tests.
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
        public Task<McpServerListResponse> ListMcpServersAsync(string? l = null, int? lim = null, string? cursor = null, CancellationToken ct = default)
            => Task.FromResult(new McpServerListResponse([]));
        public Task<McpServerQueryResponse?> QueryMcpServerAsync(string id, string? v = null, CancellationToken ct = default)
            => Task.FromResult<McpServerQueryResponse?>(null);
        public Task EvictMcpServerAsync(string id, string? v = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ContainerPluginHandle> CreateContainerPluginAsync(ContainerPluginManifest m, CancellationToken ct = default)
            => Task.FromResult(new ContainerPluginHandle(m.Id, m.Version));
        public Task<ContainerPluginHandle> UpdateContainerPluginAsync(string id, ContainerPluginManifest m, string? v = null, CancellationToken ct = default)
            => Task.FromResult(new ContainerPluginHandle(id, m.Version));
    }
}
