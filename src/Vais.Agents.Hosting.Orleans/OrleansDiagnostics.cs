// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Well-known diagnostic identifiers for the Orleans grain layer. Consumers register
/// <see cref="ActivitySourceName"/> on a <see cref="System.Diagnostics.ActivitySource"/>
/// listener — the easiest path is calling <c>AddAgenticInstrumentation</c> from the
/// <c>Vais.Agents.Observability.OpenTelemetry</c> package, which includes this source.
/// </summary>
/// <remarks>
/// If no listener is registered, <see cref="ActivitySource"/> calls are no-ops and pay
/// no allocation — zero-cost by default.
/// </remarks>
public static class OrleansDiagnostics
{
    /// <summary>
    /// ActivitySource name for <see cref="AiAgentGrain"/> lifecycle spans
    /// (<c>grain.activate</c>, <c>grain.ask</c>).
    /// </summary>
    public const string ActivitySourceName = "Vais.Agents.Hosting.Orleans";

    /// <summary>The shared <see cref="System.Diagnostics.ActivitySource"/> used by the Orleans grain layer.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
