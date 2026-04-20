// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Resolves <see cref="SecretKeyReference"/> entries from the K8s API
/// into decoded string values. Internal narrow-purpose interface; the
/// default implementation is <see cref="KubernetesSecretResolver"/>.
/// </summary>
/// <remarks>
/// Kept as a narrow abstraction to keep tests decoupled from the full
/// <c>IKubernetesClient</c> surface (dozens of methods). Production DI
/// wires <see cref="KubernetesSecretResolver"/>; tests inject fakes.
/// </remarks>
internal interface IKubernetesSecretResolver
{
    /// <summary>
    /// Resolve every entry in <paramref name="refs"/> from the K8s API
    /// in <paramref name="namespaceName"/>. Returns a map from logical
    /// name → decoded string value. Throws
    /// <see cref="SecretResolutionException"/> on the first missing
    /// reference; the caller reports it on status conditions.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string namespaceName,
        IReadOnlyDictionary<string, SecretKeyReference> refs,
        CancellationToken cancellationToken);
}

/// <summary>Thrown when a referenced <see cref="SecretKeyReference"/> can't be resolved.</summary>
internal sealed class SecretResolutionException(
    string logicalName,
    SecretKeyReference secretRef,
    string reason)
    : Exception($"Secret reference '{logicalName}' (secret '{secretRef.Name}', key '{secretRef.Key}'): {reason}")
{
    /// <summary>Logical name under <see cref="AgentSpec.SecretRefs"/> that failed to resolve.</summary>
    public string LogicalName { get; } = logicalName;

    /// <summary>The secret selector that failed.</summary>
    public SecretKeyReference SecretRef { get; } = secretRef;

    /// <summary>Human-readable reason (e.g. "secret not found", "key not present in data").</summary>
    public string Reason { get; } = reason;
}
