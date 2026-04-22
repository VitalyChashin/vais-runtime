// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// <see cref="IRemoteIdentityProvider"/> that routes to the correct provider
/// based on <see cref="RemoteRuntimeOptionsMap"/> per runtime URL.
/// Falls back to <see cref="ForwardingRemoteIdentityProvider"/> for unconfigured runtimes.
/// </summary>
public sealed class CompositeRemoteIdentityProvider : IRemoteIdentityProvider
{
    private readonly IReadOnlyDictionary<string, IRemoteIdentityProvider> _providers;
    private readonly IRemoteIdentityProvider _fallback;

    /// <summary>Construct with per-runtime providers and a fallback for unconfigured URLs.</summary>
    public CompositeRemoteIdentityProvider(
        IReadOnlyDictionary<string, IRemoteIdentityProvider> providers,
        IRemoteIdentityProvider fallback)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <inheritdoc />
    public ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
        string runtimeUrl,
        string? inboundBearerToken,
        CancellationToken cancellationToken = default)
    {
        var normalised = NormaliseUrl(runtimeUrl);
        var provider = _providers.TryGetValue(normalised, out var p) ? p : _fallback;
        return provider.AcquireOutboundTokenAsync(normalised, inboundBearerToken, cancellationToken);
    }

    private static string NormaliseUrl(string url) => url.TrimEnd('/');
}
