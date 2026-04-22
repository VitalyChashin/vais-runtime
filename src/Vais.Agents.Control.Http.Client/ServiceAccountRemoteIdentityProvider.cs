// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// <see cref="IRemoteIdentityProvider"/> that reads a Kubernetes projected
/// ServiceAccount token from a file and caches it with TTL + mtime checking.
/// Mirrors the caching pattern from <c>ServiceAccountTokenHandler</c> in the
/// K8s operator project.
/// </summary>
public sealed class ServiceAccountRemoteIdentityProvider : IRemoteIdentityProvider, IDisposable
{
    private readonly string _tokenPath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ITokenFileReader _fileReader;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedAt;
    private DateTime _cachedFileMtime;

    /// <summary>Construct with path, time provider, cache TTL, and file reader.</summary>
    public ServiceAccountRemoteIdentityProvider(
        string tokenPath,
        TimeProvider timeProvider,
        TimeSpan cacheTtl,
        ITokenFileReader? fileReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenPath);
        _tokenPath = tokenPath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = cacheTtl;
        _fileReader = fileReader ?? new FileSystemTokenReader();
    }

    /// <inheritdoc />
    public async ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
        string runtimeUrl,
        string? inboundBearerToken,
        CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return new OutboundCredential("Bearer", token);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedToken is not null && now - _cachedAt < _cacheTtl)
        {
            var currentMtime = _fileReader.GetMtime(_tokenPath);
            if (currentMtime == _cachedFileMtime)
            {
                return _cachedToken;
            }
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            var mtime = _fileReader.GetMtime(_tokenPath);
            if (_cachedToken is not null && now - _cachedAt < _cacheTtl && mtime == _cachedFileMtime)
            {
                return _cachedToken;
            }

            var token = (await _fileReader.ReadAllTextAsync(_tokenPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"ServiceAccount token at '{_tokenPath}' is empty. " +
                    "Verify the projected-volume mount is configured with a non-zero TTL.");
            }

            _cachedToken = token;
            _cachedAt = now;
            _cachedFileMtime = mtime;
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }
}

/// <summary>Narrow abstraction over token-file reads for unit testing.</summary>
public interface ITokenFileReader
{
    /// <summary>Read the entire file content as a string.</summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Get the file's last-write time in UTC, or <see cref="DateTime.MinValue"/> if not found.</summary>
    DateTime GetMtime(string path);
}

/// <summary>Production <see cref="ITokenFileReader"/> — reads from disk.</summary>
public sealed class FileSystemTokenReader : ITokenFileReader
{
    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllTextAsync(path, cancellationToken);

    /// <inheritdoc />
    public DateTime GetMtime(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}
