// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// The result of an <see cref="IModelRouter"/> lookup: the concrete provider that
/// handles the alias and the <see cref="ModelSpec"/> that describes it.
/// </summary>
public sealed record ModelRoute(
    ICompletionProvider Provider,
    ModelSpec ModelSpec);
