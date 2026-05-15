// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// HTTP-level tests for the <c>POST /v1/container-gateway/tools/invoke</c> endpoint.
/// G2: confirms tool calls traverse <see cref="IToolGuardrail"/> (before + after),
/// append a <see cref="ToolCallRecorded"/> entry to <see cref="IAgentJournal"/> under
/// the request's <c>X-Run-Id</c>, and traverse the <see cref="ToolGatewayMiddleware"/>
/// chain — the same path C# agents take via <c>StatefulAiAgent</c>'s dispatcher.
/// </summary>
public sealed class ContainerGatewayToolInvokeTests : IAsyncLifetime
{
    private const string TestSecret = "A32CharacterSecretKeyForTestingXX";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private RecordingGuardrail _guardrail = null!;
    private InMemoryAgentJournal _journal = null!;
    private RecordingToolMiddleware _middleware = null!;
    private StubTool _tool = null!;

    public async Task InitializeAsync()
    {
        _guardrail = new RecordingGuardrail();
        _journal = new InMemoryAgentJournal();
        _middleware = new RecordingToolMiddleware();
        _tool = new StubTool("echo", "ECHO:");

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    var config = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Vais:ContainerPlugin:CallTokenSecret"] = TestSecret,
                        })
                        .Build();
                    services.AddSingleton<IConfiguration>(config);
                    services.AddSingleton<ICallTokenService, HmacCallTokenService>();

                    services.AddSingleton<IMcpServerRegistry>(new SingleServerRegistry("test-server"));
                    services.AddSingleton<INamedToolSourceProvider>(new SingleSourceProvider("test-server", _tool));
                    services.AddSingleton<IToolGuardrail>(_guardrail);
                    services.AddSingleton<IAgentJournal>(_journal);
                    services.AddSingleton<ToolGatewayMiddleware>(_middleware);

                    // ICompletionProviderPool needed for the LLM endpoints in the same group
                    // (route binding inspects all endpoints at startup).
                    services.AddSingleton<ICompletionProviderPool>(new NullProviderPool());
                    services.AddSingleton<LlmGatewayMiddleware>(new NoopLlmMiddleware());

                    services.AddSingleton<AsyncLocalAgentContextAccessor>();
                    services.AddSingleton<IAgentContextAccessor>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
                    services.AddSingleton<IAgentContextSetter>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());

                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapContainerGatewayEndpoints());
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task ToolInvoke_TraversesGuardrailJournalAndMiddleware()
    {
        var (runId, agentId) = ("run-tool", "agent-tool");
        var resp = await PostInvokeAsync(runId, agentId, toolName: "echo", argsJson: "{\"x\":1}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("content").GetString().Should().Be("ECHO:{\"x\":1}");
        body.GetProperty("isError").GetBoolean().Should().BeFalse();

        _guardrail.BeforeInvocations.Should().Be(1);
        _guardrail.AfterInvocations.Should().Be(1);
        _middleware.Invocations.Should().Be(1);

        // Journal appended with the request's runId.
        var entries = new List<JournalEntry>();
        await foreach (var entry in _journal.ReadAsync(runId))
            entries.Add(entry);
        entries.Should().ContainSingle()
            .Which.Should().BeOfType<ToolCallRecorded>()
            .Which.Outcome.Result.Should().Be("ECHO:{\"x\":1}");
    }

    [Fact]
    public async Task ToolInvoke_GuardrailDeny_ShortCircuits_AndReturnsError()
    {
        _guardrail.DenyBefore = true;
        var resp = await PostInvokeAsync("run-deny", "agent-deny", "echo", "{}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isError").GetBoolean().Should().BeTrue();
        body.GetProperty("content").GetString().Should().Contain("denied by guardrail");

        _guardrail.BeforeInvocations.Should().Be(1);
        _guardrail.AfterInvocations.Should().Be(0);
        _tool.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ToolInvoke_UnknownTool_ReturnsNotFoundError()
    {
        var resp = await PostInvokeAsync("run-x", "agent-x", "nope", "{}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isError").GetBoolean().Should().BeTrue();
        body.GetProperty("content").GetString().Should().Contain("not found");

        _guardrail.BeforeInvocations.Should().Be(0);
        _tool.InvokeCount.Should().Be(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostInvokeAsync(
        string runId, string agentId, string toolName, string argsJson)
    {
        var token = _host.Services.GetRequiredService<ICallTokenService>().Generate(runId, agentId, 60);
        using var argsDoc = JsonDocument.Parse(argsJson);
        var body = new
        {
            toolName,
            arguments = argsDoc.RootElement.Clone(),
            toolCallId = "call-1",
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/tools/invoke")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);
        return await _client.SendAsync(req);
    }

    // ── stubs ────────────────────────────────────────────────────────────────

    private sealed class StubTool(string name, string prefix) : ITool
    {
        public int InvokeCount;
        public string Name => name;
        public string Description => "stub";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref InvokeCount);
            return Task.FromResult(prefix + arguments.GetRawText());
        }
    }

    private sealed class RecordingGuardrail : IToolGuardrail
    {
        public int BeforeInvocations;
        public int AfterInvocations;
        public bool DenyBefore;

        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(
            ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref BeforeInvocations);
            return ValueTask.FromResult(DenyBefore
                ? GuardrailOutcome.Deny("test-deny")
                : GuardrailOutcome.Pass);
        }

        public ValueTask<GuardrailOutcome> AfterInvokeAsync(
            ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AfterInvocations);
            return ValueTask.FromResult(GuardrailOutcome.Pass);
        }
    }

    private sealed class RecordingToolMiddleware : ToolGatewayMiddleware
    {
        public int Invocations;

        public override async Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Invocations);
            return await next();
        }
    }

    private sealed class SingleServerRegistry(string id) : IMcpServerRegistry
    {
        public IAsyncEnumerable<McpServerManifest> ListAsync(
            string? labelPrefix = null, CancellationToken ct = default)
            => Yield(new McpServerManifest(id, "1.0"));

        public ValueTask<McpServerManifest?> GetAsync(
            string id, string? version = null, CancellationToken ct = default)
            => ValueTask.FromResult<McpServerManifest?>(null);

        public ValueTask RegisterAsync(McpServerManifest manifest, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<T> Yield<T>(T value)
        {
            await Task.CompletedTask;
            yield return value;
        }
    }

    private sealed class SingleSourceProvider(string name, ITool tool) : INamedToolSourceProvider
    {
        public IToolSource? GetByName(string requestedName)
            => string.Equals(requestedName, name, StringComparison.OrdinalIgnoreCase)
                ? new InlineToolSource(tool)
                : null;
    }

    private sealed class InlineToolSource(ITool tool) : IToolSource
    {
        public async IAsyncEnumerable<ITool> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return tool;
        }
    }

    private sealed class NullProviderPool : ICompletionProviderPool
    {
        public ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("LLM endpoints not exercised in tool-invoke tests.");
    }

    private sealed class NoopLlmMiddleware : LlmGatewayMiddleware
    {
        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => next(request, cancellationToken);

        protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            CancellationToken cancellationToken)
            => next(request, cancellationToken);
    }
}
