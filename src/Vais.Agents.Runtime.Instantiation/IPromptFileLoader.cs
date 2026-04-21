// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Reads prompt content from a <see cref="SystemPromptSpec.FileRef"/>. The
/// translator invokes this before running variable substitution.
/// </summary>
/// <remarks>
/// The default implementation rooted at a configurable directory
/// (e.g. <c>/var/lib/vais/prompts/</c> in the container image) ships as
/// <c>FileSystemPromptFileLoader</c>; consumers override via DI for sandbox
/// /in-memory use cases (tests, secret-store-backed prompts, etc.).
/// </remarks>
public interface IPromptFileLoader
{
    /// <summary>
    /// Read the prompt at <paramref name="fileRef"/>. The reference is treated
    /// as an implementation-defined identifier (relative path, URL, key,
    /// etc.); the <c>FileSystemPromptFileLoader</c> interprets it as a path
    /// relative to its configured root.
    /// </summary>
    /// <exception cref="ManifestInstantiationException">On read failure — uses <see cref="ManifestInstantiationUrns.PromptFileUnreadable"/>.</exception>
    ValueTask<string> LoadAsync(string fileRef, CancellationToken cancellationToken = default);
}
