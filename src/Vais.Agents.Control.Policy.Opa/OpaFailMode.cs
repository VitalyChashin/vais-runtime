// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// Behaviour selected via <see cref="OpaPolicyEngineOptions.FailMode"/>
/// when the adapter cannot reach the target OPA process, times out, or
/// receives a malformed response. Only runtime failures trigger
/// FailMode; 4xx responses indicate adapter / config bugs and always
/// throw rather than applying FailMode.
/// </summary>
public enum OpaFailMode
{
    /// <summary>
    /// Deny on error — enterprise-safe default. Emits a
    /// <see cref="PolicyDecision.Deny"/> with the failure reason so the
    /// audit log captures the incident shape.
    /// </summary>
    Closed = 0,

    /// <summary>
    /// Allow on error — dev / single-tenant convenience. Emits
    /// <see cref="PolicyDecision.Allow"/> on failure so local runs stay
    /// unblocked when OPA isn't wired yet.
    /// </summary>
    Open = 1,
}
