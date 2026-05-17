// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// Serializes test classes that mutate <c>ApplyCommand.DockerRun</c> /
/// <c>ApplyCommand.DockerImageExists</c> static hooks. xunit parallelises across
/// classes by default; the shared collection name disables that for these specific
/// classes so they don't race on the static.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApplyCommandStaticsCollection
{
    public const string Name = "ApplyCommandStatics";
}
