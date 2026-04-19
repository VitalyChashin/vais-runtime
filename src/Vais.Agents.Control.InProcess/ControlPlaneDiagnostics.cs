// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// Telemetry primitives for the control-plane engine — one <see cref="ActivitySource"/>
/// for tracing, one <see cref="Meter"/> for metrics. Names follow the <c>vais.*</c>
/// extension of OTel GenAI conventions already used by the agent runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring.</b> Consumers attach an OTel <c>MeterProvider</c> + <c>TracerProvider</c>
/// to <see cref="SourceName"/> and <see cref="MeterName"/>; every verb emits a
/// span with duration + agent id + principal tags, plus counters / histograms
/// for latency and error rate.
/// </para>
/// </remarks>
public static class ControlPlaneDiagnostics
{
    /// <summary>ActivitySource name for control-plane verb spans.</summary>
    public const string SourceName = "Vais.Agents.Control";

    /// <summary>Meter name for control-plane counters / histograms.</summary>
    public const string MeterName = "Vais.Agents.Control";

    internal static readonly ActivitySource ActivitySource = new(SourceName);
    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> VerbDuration = Meter.CreateHistogram<double>(
        name: "vais.control.verb.duration",
        unit: "ms",
        description: "Wall-clock duration of a control-plane verb from entry to completion (or failure).");

    internal static readonly Counter<long> VerbCount = Meter.CreateCounter<long>(
        name: "vais.control.verb.count",
        unit: "{verb}",
        description: "Count of control-plane verbs dispatched, tagged by verb + outcome (allowed|denied|errored).");
}
