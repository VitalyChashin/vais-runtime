// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Resolves <c>secret://&lt;backend&gt;/&lt;path&gt;</c> URIs from an
/// <see cref="AgentManifest"/> into concrete credential values at activation
/// time. The manifest carries only the reference; implementations look up the
/// value from an environment variable, file, cloud secret store, etc.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backend routing.</b> The URI's host segment (e.g. <c>env</c>, <c>file</c>,
/// <c>keyvault</c>, <c>awssm</c>, <c>custom</c>) selects a resolver. Implementations
/// usually register per-backend and a composite resolver dispatches to the right
/// one; the contract here is runtime-neutral so a consumer can plug any strategy.
/// </para>
/// <para>
/// <b>Lifetime.</b> Resolution happens per-invocation in the MVP (no caching).
/// Secret stores that are hot-path sensitive should layer their own cache.
/// </para>
/// </remarks>
public interface ISecretResolver
{
    /// <summary>
    /// Return the value for <paramref name="secretUri"/>, or throw when the
    /// resolver can't look it up. URIs that don't match the resolver's backend
    /// scheme should throw <see cref="NotSupportedException"/> — composite
    /// resolvers catch this to fall through to the next candidate.
    /// </summary>
    /// <exception cref="NotSupportedException">URI backend scheme isn't served by this resolver.</exception>
    /// <exception cref="SecretNotFoundException">URI scheme matched but the value couldn't be found.</exception>
    ValueTask<string> ResolveAsync(string secretUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when a <see cref="ISecretResolver"/> recognises the URI scheme but
/// can't find a value at the referenced path.
/// </summary>
public sealed class SecretNotFoundException : Exception
{
    /// <summary>Create a missing-secret exception for <paramref name="secretUri"/>.</summary>
    public SecretNotFoundException(string secretUri)
        : base($"Secret not found: {secretUri}")
    {
        SecretUri = secretUri;
    }

    /// <summary>The URI that couldn't be resolved.</summary>
    public string SecretUri { get; }
}
