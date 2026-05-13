// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class ServiceAccountTokenHandlerTests
{
    [Fact]
    public async Task ServiceAccount_Mode_InjectsBearerToken()
    {
        var (handler, inner, fileReader, clock) = Build(new KubernetesOperatorOptions
        {
            TokenPath = "/var/run/secrets/tokens/vais-runtime-token",
            AuthMode = KubernetesOperatorAuthMode.ServiceAccount,
            TokenCacheTtl = TimeSpan.FromMinutes(5),
        });
        fileReader.Token = "token-one";
        fileReader.Mtime = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents");
        _ = await client.SendAsync(request);

        inner.LastRequest!.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "token-one"));
    }

    [Fact]
    public async Task ClientCredentials_Mode_SkipsTokenInjection()
    {
        var (handler, inner, _, _) = Build(new KubernetesOperatorOptions
        {
            AuthMode = KubernetesOperatorAuthMode.ClientCredentials,
        });

        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents");
        _ = await client.SendAsync(request);

        inner.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ReadsTokenOnce_WhenMtimeUnchanged()
    {
        var (handler, _, fileReader, clock) = Build(new KubernetesOperatorOptions
        {
            TokenPath = "/tokens/t",
            AuthMode = KubernetesOperatorAuthMode.ServiceAccount,
            TokenCacheTtl = TimeSpan.FromMinutes(5),
        });
        fileReader.Token = "token-one";
        fileReader.Mtime = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);

        using var client = new HttpClient(handler);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents");
            _ = await client.SendAsync(request);
        }

        fileReader.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task RefreshesToken_OnMtimeChange()
    {
        var (handler, inner, fileReader, clock) = Build(new KubernetesOperatorOptions
        {
            TokenPath = "/tokens/t",
            AuthMode = KubernetesOperatorAuthMode.ServiceAccount,
            TokenCacheTtl = TimeSpan.FromMinutes(5),
        });
        fileReader.Token = "token-one";
        fileReader.Mtime = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);

        using var client = new HttpClient(handler);
        using (var req1 = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents"))
        {
            _ = await client.SendAsync(req1);
        }

        // Kubelet rotates the token — file mtime changes.
        fileReader.Token = "token-two";
        fileReader.Mtime = new DateTime(2026, 4, 20, 12, 30, 0, DateTimeKind.Utc);

        using (var req2 = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents"))
        {
            _ = await client.SendAsync(req2);
        }

        inner.LastRequest!.Headers.Authorization!.Parameter.Should().Be("token-two");
        fileReader.ReadCount.Should().Be(2);
    }

    [Fact]
    public async Task TtlExpiry_ForcesReread_EvenWithSameMtime()
    {
        var (handler, inner, fileReader, clock) = Build(new KubernetesOperatorOptions
        {
            TokenPath = "/tokens/t",
            AuthMode = KubernetesOperatorAuthMode.ServiceAccount,
            TokenCacheTtl = TimeSpan.FromMinutes(5),
        });
        fileReader.Token = "token-one";
        fileReader.Mtime = new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc);

        using var client = new HttpClient(handler);
        using (var req1 = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents"))
        {
            _ = await client.SendAsync(req1);
        }

        // Advance past TTL. Mtime intentionally unchanged — handler should
        // still force a re-read because TTL is the primary freshness signal;
        // mtime is a secondary invalidation hint.
        clock.Advance(TimeSpan.FromMinutes(6));
        fileReader.Token = "token-one-refreshed";

        using (var req2 = new HttpRequestMessage(HttpMethod.Get, "https://runtime.local/v1/agents"))
        {
            _ = await client.SendAsync(req2);
        }

        fileReader.ReadCount.Should().Be(2);
        inner.LastRequest!.Headers.Authorization!.Parameter.Should().Be("token-one-refreshed");
    }

    private static (ServiceAccountTokenHandler handler, RecordingHandler inner, FakeTokenReader fileReader, FakeTimeProvider clock) Build(KubernetesOperatorOptions options)
    {
        var monitor = new StubOptionsMonitor(options);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        var fileReader = new FakeTokenReader();
        var handler = new ServiceAccountTokenHandler(monitor, clock, fileReader);
        var inner = new RecordingHandler();
        handler.InnerHandler = inner;
        return (handler, inner, fileReader, clock);
    }

    private sealed class StubOptionsMonitor(KubernetesOperatorOptions value) : IOptionsMonitor<KubernetesOperatorOptions>
    {
        public KubernetesOperatorOptions CurrentValue => value;

        public KubernetesOperatorOptions Get(string? name) => value;

        public IDisposable OnChange(Action<KubernetesOperatorOptions, string?> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeTokenReader : ITokenFileReader
    {
        public string Token { get; set; } = "";
        public DateTime Mtime { get; set; } = DateTime.UtcNow;
        public int ReadCount { get; private set; }

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Token);
        }

        public DateTime GetMtime(string path) => Mtime;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
