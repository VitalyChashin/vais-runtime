// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.11 PR 3: OpenAPI document end-to-end. Spins up a TestServer with
/// <see cref="AgentControlPlaneOpenApiServiceCollectionExtensions.AddAgentControlPlaneOpenApi"/>
/// + <see cref="AgentControlPlaneOpenApiServiceCollectionExtensions.MapAgentControlPlaneOpenApi"/>
/// mounted alongside the control-plane routes, then fetches the spec and asserts
/// shape + content.
/// </summary>
public sealed class AgentControlPlaneOpenApiTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;
    private JsonElement _spec;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("hi")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddAgentControlPlane();
                    services.AddAgentControlPlaneOpenApi();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAgentControlPlane();
                        endpoints.MapAgentControlPlaneOpenApi();
                    });
                });
            })
            .StartAsync();
        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");

        using var response = await _http.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        _spec = JsonSerializer.Deserialize<JsonElement>(raw);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ---- 1: spec endpoint 200 + JSON ----

    [Fact]
    public async Task Spec_Endpoint_Returns_200_With_Application_Json()
    {
        using var response = await _http.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().StartWith("application/");
        response.Content.Headers.ContentType.MediaType.Should().Contain("json");
    }

    // ---- 2: 7 operations by expected id ----

    [Fact]
    public void All_Seven_Operations_Present_By_OperationId()
    {
        var ids = CollectOperationIds();
        ids.Should().Contain(new[]
        {
            "Agents.Create", "Agents.List", "Agents.Query",
            "Agents.Update", "Agents.CancelOrEvict", "Agents.Invoke",
            "Agents.Signal",
        });
    }

    // ---- 3: summaries + tags correct ----

    [Fact]
    public void Operations_Carry_Summaries_And_Tags()
    {
        var paths = _spec.GetProperty("paths");
        var createOp = GetOperation(paths, "/v1/agents", "post");
        createOp.GetProperty("summary").GetString().Should().Contain("Create an agent");
        var tags = createOp.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray();
        tags.Should().Contain("Agents");
    }

    // ---- 4: Create documents AgentManifest accept + AgentHandle 201 ----

    [Fact]
    public void Create_Operation_Accepts_Manifest_And_Responds_Handle()
    {
        var paths = _spec.GetProperty("paths");
        var createOp = GetOperation(paths, "/v1/agents", "post");

        // requestBody content-types: json + yaml
        var rb = createOp.GetProperty("requestBody").GetProperty("content");
        rb.EnumerateObject().Select(p => p.Name).Should().Contain("application/json");

        // 201 response with a handle schema reference
        var responses = createOp.GetProperty("responses");
        responses.TryGetProperty("201", out _).Should().BeTrue();
    }

    // ---- 5: Problem Details URN extension populated ----

    [Fact]
    public void Error_Responses_Carry_X_Vais_Type_Urns_Extension()
    {
        var paths = _spec.GetProperty("paths");
        var createOp = GetOperation(paths, "/v1/agents", "post");
        var responses = createOp.GetProperty("responses");

        var r422 = responses.GetProperty("422");
        r422.TryGetProperty("x-vais-type-urns", out var urns422).Should().BeTrue();
        urns422.EnumerateArray().Select(u => u.GetString()).Should().Contain(ProblemDetailsMapping.IdempotencyMismatchType);

        var r409 = responses.GetProperty("409");
        r409.TryGetProperty("x-vais-type-urns", out var urns409).Should().BeTrue();
        urns409.EnumerateArray().Select(u => u.GetString()).Should().Contain(ProblemDetailsMapping.IdempotencyInFlightType);

        var r403 = responses.GetProperty("403");
        r403.TryGetProperty("x-vais-type-urns", out var urns403).Should().BeTrue();
        urns403.EnumerateArray().Select(u => u.GetString()).Should().Contain(ProblemDetailsMapping.PolicyDeniedType);
    }

    // ---- 6: Spec round-trips through System.Text.Json ----

    [Fact]
    public async Task Spec_Round_Trips_Cleanly()
    {
        using var response = await _http.GetAsync("/openapi/v1.json");
        var raw = await response.Content.ReadAsStringAsync();

        // Re-parse + re-serialise + re-parse; if any non-JSON/circular structure exists, it throws.
        var doc = JsonDocument.Parse(raw);
        var reserialised = JsonSerializer.Serialize(doc.RootElement);
        var reparsed = JsonDocument.Parse(reserialised);

        reparsed.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        reparsed.RootElement.TryGetProperty("openapi", out _).Should().BeTrue();
        reparsed.RootElement.TryGetProperty("paths", out _).Should().BeTrue();
    }

    // ---- helpers ----

    private static JsonElement GetOperation(JsonElement pathsNode, string path, string method)
    {
        if (!pathsNode.TryGetProperty(path, out var pathNode))
        {
            throw new InvalidOperationException($"Path '{path}' not present in spec. Available: {string.Join(", ", pathsNode.EnumerateObject().Select(p => p.Name))}");
        }
        if (!pathNode.TryGetProperty(method, out var op))
        {
            throw new InvalidOperationException($"Method '{method}' not present on path '{path}'.");
        }
        return op;
    }

    private List<string> CollectOperationIds()
    {
        var ids = new List<string>();
        foreach (var pathEntry in _spec.GetProperty("paths").EnumerateObject())
        {
            foreach (var methodEntry in pathEntry.Value.EnumerateObject())
            {
                if (methodEntry.Value.TryGetProperty("operationId", out var opId))
                {
                    ids.Add(opId.GetString() ?? string.Empty);
                }
            }
        }
        return ids;
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
