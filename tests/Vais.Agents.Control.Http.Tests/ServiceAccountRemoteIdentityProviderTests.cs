// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Http;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

public sealed class ServiceAccountRemoteIdentityProviderTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly StubTokenFileReader _file = new();

    private ServiceAccountRemoteIdentityProvider Create(
        string path = "/var/run/secrets/tokens/test-token",
        TimeSpan? cacheTtl = null)
        => new(path, _time, cacheTtl ?? TimeSpan.FromMinutes(5), _file);

    [Fact]
    public async Task AcquireOutboundToken_ReadsFile_ReturnsToken()
    {
        _file.SetFile("/var/run/secrets/tokens/test-token", "sa-token-123");
        var sut = Create();

        var result = await sut.AcquireOutboundTokenAsync("https://remote.svc", null);

        result.Kind.Should().Be("Bearer");
        result.Value.Should().Be("sa-token-123");
    }

    [Fact]
    public async Task AcquireOutboundToken_CachesToken_WithinTtl()
    {
        var mtime = new DateTime(2026, 1, 1);
        _file.SetFile("/var/run/secrets/tokens/test-token", "token-v1", mtime: mtime);
        var sut = Create();

        await sut.AcquireOutboundTokenAsync("https://remote.svc", null);
        _file.SetFile("/var/run/secrets/tokens/test-token", "token-v2", mtime: mtime);

        var result = await sut.AcquireOutboundTokenAsync("https://remote.svc", null);
        result.Value.Should().Be("token-v1"); // cached, not re-read (same mtime)
    }

    [Fact]
    public async Task AcquireOutboundToken_RereadsToken_AfterTtl()
    {
        _file.SetFile("/var/run/secrets/tokens/test-token", "token-v1");
        var sut = Create();

        await sut.AcquireOutboundTokenAsync("https://remote.svc", null);

        _time.Advance(TimeSpan.FromMinutes(6));
        _file.SetFile("/var/run/secrets/tokens/test-token", "token-v2");

        var result = await sut.AcquireOutboundTokenAsync("https://remote.svc", null);
        result.Value.Should().Be("token-v2"); // TTL expired, re-read
    }

    [Fact]
    public async Task AcquireOutboundToken_RereadsToken_OnMtimeChange()
    {
        _file.SetFile("/var/run/secrets/tokens/test-token", "token-v1", mtime: new DateTime(2026, 1, 1));
        var sut = Create();

        await sut.AcquireOutboundTokenAsync("https://remote.svc", null);

        _file.SetFile("/var/run/secrets/tokens/test-token", "token-v2", mtime: new DateTime(2026, 1, 2));

        var result = await sut.AcquireOutboundTokenAsync("https://remote.svc", null);
        result.Value.Should().Be("token-v2"); // mtime changed, re-read
    }

    [Fact]
    public async Task AcquireOutboundToken_EmptyToken_Throws()
    {
        _file.SetFile("/var/run/secrets/tokens/test-token", "   ");
        var sut = Create();

        var act = async () => await sut.AcquireOutboundTokenAsync("https://remote.svc", null);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty*");
    }

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        var act = () => new ServiceAccountRemoteIdentityProvider(null!, _time, TimeSpan.FromMinutes(5));
        act.Should().Throw<ArgumentException>();
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 4, 22, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class StubTokenFileReader : ITokenFileReader
{
    private readonly Dictionary<string, (string Content, DateTime Mtime)> _files = new(StringComparer.OrdinalIgnoreCase);

    public void SetFile(string path, string content, DateTime? mtime = null)
        => _files[path] = (content, mtime ?? DateTime.UtcNow);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        => _files.TryGetValue(path, out var entry)
            ? Task.FromResult(entry.Content)
            : throw new FileNotFoundException($"Token file not found: {path}");

    public DateTime GetMtime(string path)
        => _files.TryGetValue(path, out var entry) ? entry.Mtime : DateTime.MinValue;
}
