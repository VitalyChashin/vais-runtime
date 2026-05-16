// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Registry of known assertion kind strings for validation. Phase-E2 implementations
/// will populate this with concrete kinds; the E1 stub accepts any kind.
/// </summary>
public interface IEvalAssertionKindRegistry
{
    /// <summary>Returns <c>true</c> if the given assertion kind string is registered.</summary>
    bool IsRegistered(string kind);
}
