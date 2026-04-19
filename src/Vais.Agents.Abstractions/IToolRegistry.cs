// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Read-only catalogue of <see cref="ITool"/>s available to an agent. A registry is
/// typically resolved once per agent (e.g. scoped per tenant or per flow) and is
/// consumed by <c>StatefulAiAgent</c> on every turn.
/// </summary>
public interface IToolRegistry
{
    /// <summary>All tools known to the registry, in a stable order.</summary>
    IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Look up a tool by its <see cref="ITool.Name"/>. Returns <c>null</c> if no
    /// tool with that name is registered.
    /// </summary>
    ITool? GetByName(string name);
}
