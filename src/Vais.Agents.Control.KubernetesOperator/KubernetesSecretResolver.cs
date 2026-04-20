// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Default <see cref="IKubernetesSecretResolver"/> — reads K8s
/// <c>Secret</c> resources via the KubeOps-supplied
/// <see cref="IKubernetesClient"/> and base64-decodes the referenced
/// keys. Scoped to the CR's namespace.
/// </summary>
internal sealed class KubernetesSecretResolver(IKubernetesClient kubernetesClient) : IKubernetesSecretResolver
{
    private readonly IKubernetesClient _kubernetesClient = kubernetesClient
        ?? throw new ArgumentNullException(nameof(kubernetesClient));

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string namespaceName,
        IReadOnlyDictionary<string, SecretKeyReference> refs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentNullException.ThrowIfNull(refs);

        if (refs.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        // Group by secret name to minimise API calls — one GET per distinct secret,
        // multiple logical names can point at the same underlying Secret.
        var secrets = new Dictionary<string, V1Secret>(StringComparer.Ordinal);
        foreach (var secretName in refs.Values.Select(r => r.Name).Distinct(StringComparer.Ordinal))
        {
            V1Secret? secret;
            try
            {
                secret = await _kubernetesClient
                    .GetAsync<V1Secret>(secretName, namespaceName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var logical = refs.First(kv => kv.Value.Name == secretName).Key;
                throw new SecretResolutionException(logical, refs[logical], "secret not found in namespace");
            }

            if (secret is null)
            {
                var logical = refs.First(kv => kv.Value.Name == secretName).Key;
                throw new SecretResolutionException(logical, refs[logical], "secret not found in namespace");
            }
            secrets[secretName] = secret;
        }

        var resolved = new Dictionary<string, string>(refs.Count, StringComparer.Ordinal);
        foreach (var (logicalName, reference) in refs)
        {
            var secret = secrets[reference.Name];
            if (secret.Data is null || !secret.Data.TryGetValue(reference.Key, out var encoded) || encoded is null)
            {
                throw new SecretResolutionException(logicalName, reference, $"key '{reference.Key}' not present in secret data");
            }
            resolved[logicalName] = Encoding.UTF8.GetString(encoded);
        }
        return resolved;
    }
}
