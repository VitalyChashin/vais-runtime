// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// G4 Phase 2 — verifies that <see cref="ContainerAgentShim"/> reads the calling grain's
/// <see cref="AgentContext"/> at mint time and passes it as <see cref="AgentContextClaims"/>
/// into the call-token. The shim's <c>StreamAsync</c> already takes <c>AgentContext</c> as a
/// parameter (used directly); <c>AskAsync</c> doesn't, so it reads from
/// <see cref="IAgentContextAccessor.Current"/> instead.
/// </summary>
public sealed class ContainerAgentShimClaimsPropagationTests
{
    private static readonly JsonSerializerOptions s_webOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AskAsync_WithAccessorContext_GeneratesTokenWithClaimsFromAccessor()
    {
        // The grain pushes a populated AgentContext onto the accessor before invoking the shim.
        // The shim must capture it at mint time and pass into ICallTokenService.Generate(claims, ...).
        var pushed = new AgentContext(AgentName: "agent-1")
        {
            UserId = "user-1",
            TenantId = "tenant-acme",
            WorkspaceId = "workspace-default",
            Scopes = ["read:files", "write:files"],
            PrivilegeLevel = PrivilegeLevel.Workspace,
        };
        var accessor = Substitute.For<IAgentContextAccessor>();
        accessor.Current.Returns(pushed);

        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentContextClaims>(), Arg.Any<int>())
            .Returns("test-token");

        var (shim, handler) = MakeShim(tokenSvc, accessor);
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("hi"));

        await shim.AskAsync("ping");

        tokenSvc.Received(1).Generate(
            Arg.Any<string>(),
            "agent-1",
            Arg.Is<AgentContextClaims>(c =>
                c.UserId == "user-1"
                && c.TenantId == "tenant-acme"
                && c.WorkspaceId == "workspace-default"
                && c.PrivilegeLevel == PrivilegeLevel.Workspace
                && c.Scopes != null && c.Scopes.Count == 2),
            Arg.Any<int>());
    }

    [Fact]
    public async Task AskAsync_NoAccessor_GeneratesTokenWithEmptyClaims()
    {
        // Test rigs that don't register IAgentContextAccessor still construct successfully;
        // the shim falls back to a minimal context (AgentName only). Backwards-compat path.
        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentContextClaims>(), Arg.Any<int>())
            .Returns("test-token");

        var (shim, handler) = MakeShim(tokenSvc, accessor: null);
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("hi"));

        await shim.AskAsync("ping");

        tokenSvc.Received(1).Generate(
            Arg.Any<string>(),
            "agent-1",
            Arg.Is<AgentContextClaims>(c =>
                c.UserId == null
                && c.TenantId == null
                && c.WorkspaceId == null
                && c.Scopes == null),
            Arg.Any<int>());
    }

    [Fact]
    public async Task StreamAsync_WithContextParameter_GeneratesTokenWithClaimsFromParameter()
    {
        // StreamAsync receives AgentContext as a parameter — the accessor isn't consulted.
        // Even when an accessor IS registered with a different context, the parameter wins.
        var paramCtx = new AgentContext(AgentName: "agent-1")
        {
            UserId = "user-stream",
            WorkspaceId = "ws-stream",
            Scopes = ["stream:scope"],
        };
        var accessor = Substitute.For<IAgentContextAccessor>();
        accessor.Current.Returns(new AgentContext(AgentName: "agent-1") { UserId = "accessor-user" });

        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentContextClaims>(), Arg.Any<int>())
            .Returns("test-token");

        var (shim, handler) = MakeShim(tokenSvc, accessor);
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("hi"));
        handler.StreamHandler = _ => Task.FromResult(SseStream(["delta-1"], finalAssistantMessage: "done"));

        var events = new List<AgentEvent>();
        await foreach (var ev in shim.StreamAsync("ping", paramCtx, CancellationToken.None))
            events.Add(ev);

        tokenSvc.Received(1).Generate(
            Arg.Any<string>(),
            "agent-1",
            Arg.Is<AgentContextClaims>(c =>
                c.UserId == "user-stream"
                && c.WorkspaceId == "ws-stream"
                && c.Scopes != null && c.Scopes.Count == 1 && c.Scopes[0] == "stream:scope"),
            Arg.Any<int>());
    }

    [Fact]
    public async Task AskAsync_SessionMode_GeneratesLeaseBoundTokenWithClaims()
    {
        // Session-mode plugins use the leaseId-bearing Generate overload. Claims still travel.
        var pushed = new AgentContext(AgentName: "agent-1")
        {
            UserId = "session-user",
            PrivilegeLevel = PrivilegeLevel.Platform,
        };
        var accessor = Substitute.For<IAgentContextAccessor>();
        accessor.Current.Returns(pushed);

        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AgentContextClaims>(), Arg.Any<int>())
            .Returns("session-token");

        var session = new ContainerSessionTokenConfig(
            SessionTtlSeconds: 1800, RenewTokenTtlSeconds: 120,
            RenewTokenUrl: "http://gateway/renew", LeaseStore: new InMemoryInvokeLeaseStore());
        var (shim, handler) = MakeShim(tokenSvc, accessor, sessionConfig: session);
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("hi"));

        await shim.AskAsync("ping");

        tokenSvc.Received(1).Generate(
            Arg.Any<string>(),
            "agent-1",
            Arg.Any<string>(),
            Arg.Is<AgentContextClaims>(c =>
                c.UserId == "session-user"
                && c.PrivilegeLevel == PrivilegeLevel.Platform),
            Arg.Any<int>());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static HttpResponseMessage OkInvoke(string assistantMessage) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { assistantMessage, opaqueState = (object?)null }, options: s_webOpts),
        };

    private static HttpResponseMessage SseStream(IReadOnlyList<string> deltas, string finalAssistantMessage)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var d in deltas)
        {
            sb.Append("event: delta\n");
            sb.Append("data: ").Append(JsonSerializer.Serialize(new { text = d }, s_webOpts)).Append('\n').Append('\n');
        }
        sb.Append("event: done\n");
        sb.Append("data: ").Append(JsonSerializer.Serialize(
            new { assistantMessage = finalAssistantMessage, opaqueState = (object?)null }, s_webOpts)).Append('\n').Append('\n');

        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), System.Text.Encoding.UTF8, "text/event-stream"),
        };
        return resp;
    }

    private static (ContainerAgentShim Shim, ClaimsTestHttpHandler Handler) MakeShim(
        ICallTokenService tokenSvc,
        IAgentContextAccessor? accessor,
        ContainerSessionTokenConfig? sessionConfig = null)
    {
        var handler = new ClaimsTestHttpHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };

        var manifest = new AgentManifest("agent-1", "1.0", new AgentHandlerRef("Test"), [], []);

        var shim = new ContainerAgentShim(
            supervisor: null!,
            invokeClient: httpClient,
            preprocessors: [],
            manifest: manifest,
            callTokenService: tokenSvc,
            internalLlmGatewayUrl: "http://gateway/llm",
            internalToolGatewayUrl: "http://gateway/tools",
            invokeTimeoutSeconds: 60,
            sessionConfig: sessionConfig,
            invokeIdleTimeoutSeconds: null,
            contextAccessor: accessor,
            logger: NullLogger.Instance);

        return (shim, handler);
    }

    private sealed class ClaimsTestHttpHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> InvokeHandler { get; set; }
            = _ => Task.FromResult(OkInvoke("default"));

        internal Func<HttpRequestMessage, Task<HttpResponseMessage>>? StreamHandler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.PathAndQuery is "/health" or "/health/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/v1/stream", StringComparison.OrdinalIgnoreCase) && StreamHandler is not null)
                return StreamHandler(request);

            return InvokeHandler(request);
        }
    }
}
