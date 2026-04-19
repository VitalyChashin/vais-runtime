// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// Resolves <c>secret://env/VAR_NAME</c> URIs from environment variables.
/// Convenient for local dev and container deployments that bake secrets into
/// env vars via a runtime secret manager (Kubernetes secrets mounted as env,
/// Docker secrets, etc).
/// </summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    internal const string Scheme = "env";

    /// <inheritdoc />
    public ValueTask<string> ResolveAsync(string secretUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretUri);
        var path = SecretUri.ExpectScheme(secretUri, Scheme);
        var value = Environment.GetEnvironmentVariable(path);
        if (value is null)
        {
            throw new SecretNotFoundException(secretUri);
        }
        return ValueTask.FromResult(value);
    }
}

/// <summary>
/// Resolves <c>secret://file/path/to/secret</c> URIs by reading the file
/// (trimming trailing whitespace — Docker / K8s secret files typically end with
/// a newline). Path is passed to <see cref="File.ReadAllText(string)"/> with
/// no further resolution; consumers supply absolute or host-relative paths.
/// </summary>
public sealed class FileSecretResolver : ISecretResolver
{
    internal const string Scheme = "file";

    /// <inheritdoc />
    public async ValueTask<string> ResolveAsync(string secretUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretUri);
        var path = SecretUri.ExpectScheme(secretUri, Scheme);
        if (!File.Exists(path))
        {
            throw new SecretNotFoundException(secretUri);
        }
        var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return raw.TrimEnd();
    }
}

/// <summary>
/// Dispatches to per-backend <see cref="ISecretResolver"/>s keyed by URI scheme.
/// The default composition ships with <see cref="EnvironmentSecretResolver"/> +
/// <see cref="FileSecretResolver"/>; custom backends (KeyVault, AWS Secrets
/// Manager, consumer-owned) plug in by adding entries.
/// </summary>
public sealed class CompositeSecretResolver : ISecretResolver
{
    private readonly IReadOnlyDictionary<string, ISecretResolver> _byScheme;

    /// <summary>Construct from a scheme → resolver map.</summary>
    public CompositeSecretResolver(IReadOnlyDictionary<string, ISecretResolver> byScheme)
    {
        ArgumentNullException.ThrowIfNull(byScheme);
        _byScheme = byScheme;
    }

    /// <summary>Convenience factory: env + file resolvers in one.</summary>
    public static CompositeSecretResolver CreateDefault() => new(new Dictionary<string, ISecretResolver>
    {
        [EnvironmentSecretResolver.Scheme] = new EnvironmentSecretResolver(),
        [FileSecretResolver.Scheme] = new FileSecretResolver(),
    });

    /// <inheritdoc />
    public ValueTask<string> ResolveAsync(string secretUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretUri);
        var scheme = SecretUri.ParseScheme(secretUri);
        if (!_byScheme.TryGetValue(scheme, out var resolver))
        {
            throw new NotSupportedException($"No resolver registered for secret scheme '{scheme}'.");
        }
        return resolver.ResolveAsync(secretUri, cancellationToken);
    }
}

internal static class SecretUri
{
    /// <summary>Extract the scheme segment from a <c>secret://scheme/path</c> URI.</summary>
    public static string ParseScheme(string secretUri)
    {
        if (!secretUri.StartsWith("secret://", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Not a secret URI: '{secretUri}'");
        }
        var rest = secretUri["secret://".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? rest : rest[..slash];
    }

    /// <summary>Validate that <paramref name="secretUri"/> uses <paramref name="expectedScheme"/> and return the path after it.</summary>
    public static string ExpectScheme(string secretUri, string expectedScheme)
    {
        var scheme = ParseScheme(secretUri);
        if (!string.Equals(scheme, expectedScheme, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Expected secret scheme '{expectedScheme}'; got '{scheme}' from '{secretUri}'.");
        }
        var prefix = $"secret://{expectedScheme}/";
        return secretUri.Length > prefix.Length ? secretUri[prefix.Length..] : string.Empty;
    }
}
