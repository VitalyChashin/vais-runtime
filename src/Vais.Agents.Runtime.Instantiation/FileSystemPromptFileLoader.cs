// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Default <see cref="IPromptFileLoader"/> — reads from a configured root
/// directory with path-traversal protection. Intended to point at the
/// runtime's prompt-bundle mount (e.g. <c>/var/lib/vais/prompts/</c>).
/// </summary>
public sealed class FileSystemPromptFileLoader : IPromptFileLoader
{
    private readonly string _rootPath;

    /// <summary>Construct a loader rooted at <paramref name="rootPath"/>.</summary>
    public FileSystemPromptFileLoader(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    /// <inheritdoc />
    public async ValueTask<string> LoadAsync(string fileRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileRef);

        string fullPath;
        try
        {
            var combined = Path.Combine(_rootPath, fileRef);
            fullPath = Path.GetFullPath(combined);
        }
        catch (Exception ex)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.PromptFileUnreadable,
                $"Prompt file reference '{fileRef}' could not be resolved: {ex.Message}",
                ex);
        }

        // Path traversal guard — resolved path must stay under the configured root.
        if (!fullPath.StartsWith(_rootPath, StringComparison.Ordinal))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.PromptFileUnreadable,
                $"Prompt file reference '{fileRef}' resolves outside the configured prompt root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.PromptFileUnreadable,
                $"Prompt file '{fileRef}' was not found under the prompt root.");
        }

        try
        {
            return await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.PromptFileUnreadable,
                $"Prompt file '{fileRef}' could not be read: {ex.Message}",
                ex);
        }
    }
}
