// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Phase-E1 stub. No assertion-kind cross-checks are performed until E2 populates
/// <see cref="IEvalAssertionKindRegistry"/> with concrete kinds.
/// </summary>
internal sealed class EmptyEvalAssertionKindRegistry : IEvalAssertionKindRegistry
{
    public bool IsRegistered(string kind) => true;
}
