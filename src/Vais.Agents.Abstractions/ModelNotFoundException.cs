// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Thrown by <see cref="IModelRouter"/> when a requested model alias has no
/// registered route. Maps to HTTP 404 on the OpenAI-compatible transport.
/// </summary>
public sealed class ModelNotFoundException : Exception
{
    /// <summary>The alias that could not be resolved.</summary>
    public string ModelAlias { get; }

    /// <summary>Construct an exception for the given unknown alias.</summary>
    public ModelNotFoundException(string modelAlias)
        : base($"Model alias '{modelAlias}' is not registered in the model router.")
    {
        ModelAlias = modelAlias;
    }
}
