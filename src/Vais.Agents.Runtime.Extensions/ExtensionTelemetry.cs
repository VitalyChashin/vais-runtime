// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Well-known diagnostic identifiers for the extensions runtime.
/// Mirrors <see cref="Vais.Agents.Core.AgenticDiagnostics"/> but scoped to extensions.
/// Consumers wire these up via <c>.AddSource("Vais.Agents.Extensions")</c> and
/// <c>.AddMeter("Vais.Agents.Extensions")</c> on their OTel builder.
/// </summary>
internal static class ExtensionTelemetry
{
    public const string ActivitySourceName = "Vais.Agents.Extensions";
    public const string MeterName = "Vais.Agents.Extensions";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Duration of a single extension handler invocation, in seconds.</summary>
    public static readonly Histogram<double> InvokeDuration =
        Meter.CreateHistogram<double>(
            "vais_extension_handler_invoke_duration_seconds",
            unit: "s",
            description: "Duration of a single extension handler invocation.");

    /// <summary>Count of extension handler invocations, split by action label.</summary>
    public static readonly Counter<long> InvokeTotal =
        Meter.CreateCounter<long>(
            "vais_extension_handler_invoke_total",
            description: "Count of extension handler invocations by action.");
}
