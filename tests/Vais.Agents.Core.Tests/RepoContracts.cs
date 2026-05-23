// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Locates <c>agentic/contracts/</c> by walking up from the test assembly's runtime
/// directory to the repo root (the directory holding <c>Vais.Agents.sln</c>). Uses
/// <see cref="AppContext.BaseDirectory"/> rather than <c>[CallerFilePath]</c> because CI
/// builds with deterministic source paths, which remap <c>[CallerFilePath]</c> to a
/// synthetic <c>/_/</c> root that doesn't exist on disk.
/// </summary>
internal static class RepoContracts
{
    public static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vais.Agents.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                $"Could not locate the repo root (Vais.Agents.sln) from {AppContext.BaseDirectory}.");
        return Path.Combine(dir.FullName, "contracts");
    }
}
