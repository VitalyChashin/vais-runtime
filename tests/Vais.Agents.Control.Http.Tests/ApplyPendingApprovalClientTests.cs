// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// NB-13 client path: when the server returns 202 + approval ProblemDetails on a high-risk apply,
/// the typed client throws <see cref="ApprovalRequiredException"/> with the correct requestId/kind/name
/// instead of falling through to a null-handle <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class ApplyPendingApprovalClientTests
{
    private const string RequestId = "req-test-1";
    private const string PluginKind = "ContainerPlugin";
    private const string ExtensionKind = "Extension";
    private const string PluginName = "test-plugin";
    private const string ExtensionName = "test-extension";

    // Mirrors the real server wire format (ProblemDetailsMapping.ToResult). The integer
    // "status": 202 is the RFC 7807 field; "approvalStatus" is the informational extension.
    // Earlier revisions of this fixture used a fictional "status_ext" key to dodge an
    // int<->string collision in the typed-record client deserializer — that papered over the
    // bug instead of catching it. Keep this body byte-for-byte aligned with the server.
    private static string ApprovalBody(string kind, string name, string requestId) =>
        $$"""{"type":"urn:vais-agents:approval-required","title":"Approval required","status":202,"requestId":"{{requestId}}","kind":"{{kind}}","name":"{{name}}","approvalStatus":"pending-approval"}""";

    private static AgentControlPlaneClient ClientThatReturns(int statusCode, string body, string contentType = "application/problem+json") =>
        new(new HttpClient(new FixedResponseHandler(statusCode, body, contentType)) { BaseAddress = new Uri("http://localhost") });

    // ---- ContainerPlugin create ----

    [Fact]
    public async Task CreateContainerPlugin_202_ThrowsApprovalRequired()
    {
        var client = ClientThatReturns(202, ApprovalBody(PluginKind, PluginName, RequestId));
        var manifest = new ContainerPluginManifest(PluginName, "1.0") { Spec = new ContainerPluginSpec { Image = "test:latest" } };

        var act = () => client.CreateContainerPluginAsync(manifest, cancellationToken: default);

        var ex = await act.Should().ThrowAsync<ApprovalRequiredException>();
        ex.Which.RequestId.Should().Be(RequestId);
        ex.Which.Kind.Should().Be(PluginKind);
        ex.Which.Name.Should().Be(PluginName);
    }

    // ---- ContainerPlugin update ----

    [Fact]
    public async Task UpdateContainerPlugin_202_ThrowsApprovalRequired()
    {
        var client = ClientThatReturns(202, ApprovalBody(PluginKind, PluginName, RequestId));
        var manifest = new ContainerPluginManifest(PluginName, "1.0") { Spec = new ContainerPluginSpec { Image = "test:latest" } };

        var act = () => client.UpdateContainerPluginAsync(PluginName, manifest, version: null, cancellationToken: default);

        var ex = await act.Should().ThrowAsync<ApprovalRequiredException>();
        ex.Which.RequestId.Should().Be(RequestId);
        ex.Which.Kind.Should().Be(PluginKind);
        ex.Which.Name.Should().Be(PluginName);
    }

    // ---- Extension apply ----

    [Fact]
    public async Task ApplyExtension_202_ThrowsApprovalRequired()
    {
        var client = ClientThatReturns(202, ApprovalBody(ExtensionKind, ExtensionName, RequestId));

        var act = () => client.ApplyExtensionAsync("apiVersion: v1\nkind: Extension\nid: test-extension", dllStream: null, cancellationToken: default);

        var ex = await act.Should().ThrowAsync<ApprovalRequiredException>();
        ex.Which.RequestId.Should().Be(RequestId);
        ex.Which.Kind.Should().Be(ExtensionKind);
        ex.Which.Name.Should().Be(ExtensionName);
    }

    // ---- Regression: 201 happy path is unaffected ----

    [Fact]
    public async Task CreateContainerPlugin_201_ReturnsHandle()
    {
        const string happyBody = """{"handle":{"id":"test-plugin","version":"1.0"},"warnings":[]}""";
        var client = ClientThatReturns(201, happyBody, "application/json");
        var manifest = new ContainerPluginManifest(PluginName, "1.0") { Spec = new ContainerPluginSpec { Image = "test:latest" } };

        var handle = await client.CreateContainerPluginAsync(manifest, cancellationToken: default);

        handle.Id.Should().Be(PluginName);
        handle.Version.Should().Be("1.0");
    }

    // ---- Legacy collision shape: pre-rename wire format must still parse cleanly ----

    [Fact]
    public async Task CreateContainerPlugin_202_WithLegacyCollidingStatusExtension_StillThrowsApprovalRequired()
    {
        // Reproduces the exact bug from plans/completed/client-detect-approval-pending-parse-bug-2026-05-26.md:
        // an older revision of the server emitted `pd.Extensions["status"] = "pending-approval"`, which
        // serialized to the same JSON key as the RFC 7807 integer `status: 202`. The typed-record
        // deserializer (ProblemDetailsWire.Status: int?) used to choke on the trailing string, throw
        // JsonException, get swallowed, and the 202 fell through. The JsonDocument-based helper reads
        // explicit fields by name and is robust to the duplicate key (last-wins per the JSON spec — but
        // the field we read is "type", which has no collision either way). This test would have failed
        // against the pre-fix helper.
        const string legacyBody =
            $$"""{"type":"urn:vais-agents:approval-required","title":"Approval required","status":202,"requestId":"{{RequestId}}","kind":"{{PluginKind}}","name":"{{PluginName}}","status":"pending-approval"}""";
        var client = ClientThatReturns(202, legacyBody);
        var manifest = new ContainerPluginManifest(PluginName, "1.0") { Spec = new ContainerPluginSpec { Image = "test:latest" } };

        var act = () => client.CreateContainerPluginAsync(manifest, cancellationToken: default);

        var ex = await act.Should().ThrowAsync<ApprovalRequiredException>();
        ex.Which.RequestId.Should().Be(RequestId);
        ex.Which.Kind.Should().Be(PluginKind);
        ex.Which.Name.Should().Be(PluginName);
    }

    // ---- Fall-through: a 202 that is NOT an approval hold must not throw ----

    [Fact]
    public async Task CreateContainerPlugin_202_WithUnrelatedProblemType_DoesNotThrowApprovalRequired()
    {
        // 202 with a Problem Details body whose `type:` URN is something other than
        // approval-required. The helper must not throw ApprovalRequiredException; the
        // happy-path deserialize then runs against the buffered body. Since the body is
        // not a ContainerPluginApplyResponse, CreateContainerPluginAsync will surface
        // either an InvalidOperationException (null handle) or a JsonException — either
        // way it must NOT be ApprovalRequiredException. This pins the fall-through
        // contract so a future helper refactor can't silently reintroduce the original
        // bug shape.
        const string body = """{"type":"urn:vais-agents:something-else","title":"Other","status":202,"detail":"not approval"}""";
        var client = ClientThatReturns(202, body);
        var manifest = new ContainerPluginManifest(PluginName, "1.0") { Spec = new ContainerPluginSpec { Image = "test:latest" } };

        var act = () => client.CreateContainerPluginAsync(manifest, cancellationToken: default);

        await act.Should().NotThrowAsync<ApprovalRequiredException>();
    }

    // ---- helpers ----

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly int _status;
        private readonly string _body;
        private readonly string _contentType;

        public FixedResponseHandler(int status, string body, string contentType)
        { _status = status; _body = body; _contentType = contentType; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage((HttpStatusCode)_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            };
            return Task.FromResult(response);
        }
    }
}
