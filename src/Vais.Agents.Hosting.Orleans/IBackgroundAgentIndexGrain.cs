// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Durable index of all background sub-run handles for a given parent run.
/// Grain key = <c>parentRunId</c>.
/// </summary>
public interface IBackgroundAgentIndexGrain : IGrainWithStringKey
{
    /// <summary>Register a new handle in this parent's index.</summary>
    Task AddHandleAsync(string handle);

    /// <summary>Return all handles registered under this parent run id.</summary>
    Task<IReadOnlyList<string>> ListHandlesAsync();
}
