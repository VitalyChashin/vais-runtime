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

    private static string ApprovalBody(string kind, string name, string requestId) =>
        $$"""{"type":"urn:vais-agents:approval-required","title":"Approval required","status":202,"requestId":"{{requestId}}","kind":"{{kind}}","name":"{{name}}","status_ext":"pending-approval"}""";

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
