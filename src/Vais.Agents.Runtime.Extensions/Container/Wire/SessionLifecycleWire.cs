// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for the <c>sessionLifecycle</c> notification sent to <c>/handlers/&lt;id&gt;/pre</c>.
/// Fire-and-forget: there is no response body (the hook is observe-only). Field names serialize camelCase.
/// </summary>
internal sealed record SessionLifecycleRequest(
    string CallId,
    SessionLifecycleContextWire Context);

internal sealed record SessionLifecycleContextWire(
    string AgentId,
    string SessionId,
    string Phase,
    int TurnCount,
    IReadOnlyList<SessionTurnWire>? History);

internal sealed record SessionTurnWire(
    string Role,
    string Text);
