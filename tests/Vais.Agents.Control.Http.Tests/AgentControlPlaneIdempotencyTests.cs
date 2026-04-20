// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.11 PR 1: idempotency middleware + InMemoryIdempotencyStore end-to-end.
/// Drives real HTTP through the TestServer with the middleware mounted in its
/// documented position (after auth, before routing).
/// </summary>
public sealed class AgentControlPlaneIdempotencyTests
{
    private static readonly AgentManifest SampleManifest = new(
        "smoke", "1.0",
        new AgentHandlerRef("declarative"),
        new[] { new ProtocolBinding("Http") },
        Array.Empty<ToolRef>());

    // ---- 1: no header, pass-through ----

    [Fact]
    public async Task No_Header_Pass_Through_No_Cache_Entry()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();

        // Same envelope body as PostManifestAsync but without the Idempotency-Key header.
        var json = JsonSerializer.Serialize(new
        {
            apiVersion = "vais.agents/v1",
            kind = "Agent",
            metadata = new { id = SampleManifest.Id, version = SampleManifest.Version },
            spec = new { handler = new { typeName = SampleManifest.Handler.TypeName } },
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/v1/agents", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // No Idempotency-Replayed header present because middleware didn't touch the response.
        response.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
    }

    // ---- 2: cache miss then replay on matching body ----

    [Fact]
    public async Task Matching_Body_Replays_Cached_Response()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();
        const string key = "replay-key";

        var first = await PostManifestAsync(http, SampleManifest, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        HeaderValue(first, "Idempotency-Replayed").Should().Be("false");
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await PostManifestAsync(http, SampleManifest, key);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        HeaderValue(second, "Idempotency-Replayed").Should().Be("true");
        var secondBody = await second.Content.ReadAsStringAsync();

        secondBody.Should().Be(firstBody);
    }

    // ---- 3: mismatched body -> 422 ----

    [Fact]
    public async Task Mismatched_Body_Returns_422_IdempotencyMismatch()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();
        const string key = "mismatch-key";

        var first = await PostManifestAsync(http, SampleManifest, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var other = SampleManifest with { Id = "other" };
        var second = await PostManifestAsync(http, other, key);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be(ProblemDetailsMapping.IdempotencyMismatchType);
        problem.GetProperty("idempotencyKey").GetString().Should().Be(key);
    }

    // ---- 4: in-flight -> 409 with Retry-After ----

    [Fact]
    public async Task InFlight_Returns_409_With_RetryAfter()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();
        var store = host.Services.GetRequiredService<IIdempotencyStore>();

        // Manually reserve the key at the same scope the middleware uses.
        var key = new IdempotencyKey(TenantId: null, Method: "POST", Path: "/v1/agents", Key: "inflight-key");
        var reserve = await store.TryBeginAsync(key, "pretend-fingerprint", default);
        reserve.Status.Should().Be(IdempotencyBeginStatus.New);

        // Now a real HTTP POST with the same key hits the middleware and observes InFlight.
        var response = await PostManifestAsync(http, SampleManifest, "inflight-key");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.RetryAfter.Should().NotBeNull();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be(ProblemDetailsMapping.IdempotencyInFlightType);
    }

    // ---- 5: 5xx handler releases reservation ----

    [Fact]
    public async Task Failing_Handler_Releases_Reservation_Allowing_Retry()
    {
        using var host = await StartHostAsync(throwingRegistry: true);
        var http = host.GetTestClient();
        const string key = "release-key";

        // First attempt — handler throws; middleware releases.
        var first = await PostManifestAsync(http, SampleManifest, key);
        first.StatusCode.Should().BeOneOf(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable);

        // Swap in the non-throwing registry by replacing the service -- not possible at runtime
        // without a second host. For this test we verify the entry was released by directly
        // inspecting the store after the failure.
        var store = host.Services.GetRequiredService<IIdempotencyStore>();
        var scope = new IdempotencyKey(null, "POST", "/v1/agents", key);
        var probe = await store.TryBeginAsync(scope, "any-fingerprint", default);
        probe.Status.Should().Be(IdempotencyBeginStatus.New, "the failing handler released the reservation");
        await store.ReleaseAsync(scope, default);
    }

    // ---- 6: 4xx handler caches response ----

    [Fact]
    public async Task Four_Xx_Response_Is_Cached_And_Replayed()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();
        const string key = "badreq-key";

        // Valid-JSON but manifest-invalid body (empty id) → 400 via AgentManifestValidationException.
        var invalidManifest = """{"apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"","version":"1.0"}}""";

        var content = new StringContent(invalidManifest, Encoding.UTF8, "application/json");
        content.Headers.Add("Idempotency-Key", key);

        var first = await http.PostAsync("/v1/agents", content);
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HeaderValue(first, "Idempotency-Replayed").Should().Be("false");

        var content2 = new StringContent(invalidManifest, Encoding.UTF8, "application/json");
        content2.Headers.Add("Idempotency-Key", key);
        var second = await http.PostAsync("/v1/agents", content2);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HeaderValue(second, "Idempotency-Replayed").Should().Be("true");
    }

    // ---- 7: GET with header is pass-through ----

    [Fact]
    public async Task Get_With_Header_Passes_Through()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/agents");
        request.Headers.Add("Idempotency-Key", "should-be-ignored");
        var response = await http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
    }

    // ---- 8: /healthz excluded ----

    [Fact]
    public async Task Healthz_With_Header_Passes_Through()
    {
        using var host = await StartHostAsync();
        var http = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/healthz");
        request.Headers.Add("Idempotency-Key", "should-be-ignored");
        var response = await http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
    }

    // ---- 9: tenant-scope isolation ----

    [Fact]
    public async Task Tenant_Scope_Isolation_Between_Different_Tenants()
    {
        using var host = await StartHostAsync();
        var store = host.Services.GetRequiredService<IIdempotencyStore>();
        var scopeA = new IdempotencyKey(TenantId: "tenant-A", Method: "POST", Path: "/v1/agents", Key: "shared-key");
        var scopeB = new IdempotencyKey(TenantId: "tenant-B", Method: "POST", Path: "/v1/agents", Key: "shared-key");

        var a = await store.TryBeginAsync(scopeA, "fpA", default);
        var b = await store.TryBeginAsync(scopeB, "fpB", default);

        a.Status.Should().Be(IdempotencyBeginStatus.New);
        b.Status.Should().Be(IdempotencyBeginStatus.New, "different tenants share a key namespace only within a single tenant");
    }

    // ---- 10: TTL expiry ----

    [Fact]
    public async Task Completed_Entry_Expires_After_Ttl()
    {
        var fakeTime = new FakeTimeProvider();
        using var host = await StartHostAsync(timeProvider: fakeTime, ttl: TimeSpan.FromMilliseconds(50));
        var store = host.Services.GetRequiredService<IIdempotencyStore>();
        var key = new IdempotencyKey(null, "POST", "/v1/agents", "ttl-key");

        (await store.TryBeginAsync(key, "fp1", default)).Status.Should().Be(IdempotencyBeginStatus.New);
        await store.CompleteAsync(key, new CachedResponse(201, "application/json", new byte[] { 1 }, fakeTime.GetUtcNow()), default);

        // Immediately after Complete, replay works.
        (await store.TryBeginAsync(key, "fp1", default)).Status.Should().Be(IdempotencyBeginStatus.Replay);

        // Advance past TTL.
        fakeTime.Advance(TimeSpan.FromMilliseconds(100));

        // Expired — next Begin is treated as New.
        (await store.TryBeginAsync(key, "fp1", default)).Status.Should().Be(IdempotencyBeginStatus.New);
    }

    // ---- helpers ----

    private static string? HeaderValue(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static async Task<HttpResponseMessage> PostManifestAsync(HttpClient http, AgentManifest manifest, string idempotencyKey)
    {
        var json = JsonSerializer.Serialize(new
        {
            apiVersion = "vais.agents/v1",
            kind = "Agent",
            metadata = new { id = manifest.Id, version = manifest.Version },
            spec = new { handler = new { typeName = manifest.Handler.TypeName } },
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.Add("Idempotency-Key", idempotencyKey);
        return await http.PostAsync("/v1/agents", content);
    }

    private static Task<IHost> StartHostAsync(
        bool throwingRegistry = false,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("hi")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry>(_ =>
                        throwingRegistry ? new ThrowingRegistry() : new InMemoryAgentRegistry());
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddAgentControlPlane();
                    services.AddAgentControlPlaneIdempotency(options =>
                    {
                        options.EvictionInterval = TimeSpan.Zero; // disable background timer in tests
                        options.Ttl = ttl ?? TimeSpan.FromHours(24);
                    });
                    if (timeProvider is not null)
                    {
                        // Replace the InMemoryIdempotencyStore registration with one using the fake clock.
                        services.RemoveAll<IIdempotencyStore>();
                        services.AddSingleton<IIdempotencyStore>(sp =>
                            new InMemoryIdempotencyStore(sp.GetRequiredService<IOptions<IdempotencyOptions>>(), timeProvider));
                    }
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAgentControlPlaneIdempotency();
                    app.UseEndpoints(endpoints => endpoints.MapAgentControlPlane());
                });
            })
            .StartAsync();
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }

    /// <summary>
    /// Registry that throws on its reflection-invoked <c>Register(AgentManifest)</c>
    /// method so <see cref="AgentLifecycleManager.CreateAsync"/> surfaces a 5xx via
    /// <see cref="ProblemDetailsMapping"/> — exercise for the release-reservation path.
    /// </summary>
    private sealed class ThrowingRegistry : IAgentRegistry
    {
        /// <summary>Invoked via reflection by <c>AgentLifecycleManager.CreateAsync</c>; throws to force a 5xx.</summary>
        public void Register(AgentManifest manifest)
            => throw new InvalidOperationException("registry is deliberately broken for the release-reservation test");

        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentManifest?>(null);

        public IAsyncEnumerable<AgentManifest> ListAsync(string? labelPrefix = null, CancellationToken cancellationToken = default)
            => EmptyAsync();

#pragma warning disable CS1998
        private static async IAsyncEnumerable<AgentManifest> EmptyAsync()
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
