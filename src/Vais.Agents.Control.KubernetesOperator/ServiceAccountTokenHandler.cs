// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// <see cref="DelegatingHandler"/> that injects a projected Kubernetes
/// ServiceAccount token into every outbound request as
/// <c>Authorization: Bearer &lt;token&gt;</c>. Matches the
/// in-cluster-auth flow documented for the v0.13 operator Helm chart.
/// </summary>
/// <remarks>
/// <para>
/// The token file is read from <see cref="KubernetesOperatorOptions.TokenPath"/>.
/// Kubelet rotates projected tokens atomically by replacing the file;
/// this handler caches the read for <see cref="KubernetesOperatorOptions.TokenCacheTtl"/>
/// and re-reads when either the TTL expires or the file's mtime changes.
/// </para>
/// <para>
/// Wired into DI via
/// <c>services.AddHttpClient&lt;IAgentControlPlaneClient, AgentControlPlaneClient&gt;()
/// .AddHttpMessageHandler&lt;ServiceAccountTokenHandler&gt;()</c>. The
/// default <c>AddAgentKubernetesOperator</c> extension does this
/// automatically when
/// <see cref="KubernetesOperatorOptions.AuthMode"/> is
/// <see cref="KubernetesOperatorAuthMode.ServiceAccount"/>.
/// </para>
/// </remarks>
public sealed class ServiceAccountTokenHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<KubernetesOperatorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITokenFileReader _fileReader;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedAt;
    private DateTime _cachedFileMtime;

    /// <summary>Construct with standard options + filesystem access.</summary>
    public ServiceAccountTokenHandler(IOptionsMonitor<KubernetesOperatorOptions> options)
        : this(options, TimeProvider.System, new FileSystemTokenReader())
    {
    }

    internal ServiceAccountTokenHandler(
        IOptionsMonitor<KubernetesOperatorOptions> options,
        TimeProvider timeProvider,
        ITokenFileReader fileReader)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _fileReader = fileReader ?? throw new ArgumentNullException(nameof(fileReader));
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;
        if (options.AuthMode != KubernetesOperatorAuthMode.ServiceAccount)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = await GetTokenAsync(options, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetTokenAsync(KubernetesOperatorOptions options, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedToken is not null && now - _cachedAt < options.TokenCacheTtl)
        {
            var currentMtime = _fileReader.GetMtime(options.TokenPath);
            if (currentMtime == _cachedFileMtime)
            {
                return _cachedToken;
            }
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            var mtime = _fileReader.GetMtime(options.TokenPath);
            if (_cachedToken is not null && now - _cachedAt < options.TokenCacheTtl && mtime == _cachedFileMtime)
            {
                return _cachedToken;
            }

            var token = (await _fileReader.ReadAllTextAsync(options.TokenPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"ServiceAccount token at '{options.TokenPath}' is empty. " +
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
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lock.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Narrow abstraction over token-file reads for unit testing.</summary>
internal interface ITokenFileReader
{
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    DateTime GetMtime(string path);
}

/// <summary>Production <see cref="ITokenFileReader"/> — reads from disk.</summary>
internal sealed class FileSystemTokenReader : ITokenFileReader
{
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllTextAsync(path, cancellationToken);

    public DateTime GetMtime(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}
