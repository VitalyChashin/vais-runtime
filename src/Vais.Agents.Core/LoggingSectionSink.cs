// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vais.Agents;

/// <summary>
/// <see cref="ISectionTelemetrySink"/> that writes one structured <see cref="LogLevel.Information"/>
/// entry per turn. Each entry carries top-level scalar fields (agent, run, turn, section count,
/// budget used / dropped / truncated) plus a JSON-serialised per-section breakdown. Log shippers
/// (Serilog, NLog, ELK) extract the scalar fields automatically for filtering; the JSON blob
/// preserves per-section detail for deep inspection.
/// </summary>
/// <remarks>
/// <para>
/// Structured-log convention: each <c>{Name}</c> placeholder in the message template is captured
/// as a property by Microsoft.Extensions.Logging. Sinks that understand structured logging
/// surface them as discrete fields; plain-text sinks render them inline.
/// </para>
/// <para>
/// The sink short-circuits when <see cref="LogLevel.Information"/> is disabled so the JSON
/// serialisation cost is paid only when the entry will actually land somewhere.
/// </para>
/// </remarks>
public sealed class LoggingSectionSink : ISectionTelemetrySink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<LoggingSectionSink> _logger;

    /// <summary>Create a logging sink writing to <paramref name="logger"/>.</summary>
    /// <param name="logger">Target logger. Must not be null.</param>
    public LoggingSectionSink(ILogger<LoggingSectionSink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return ValueTask.CompletedTask;
        }

        _logger.LogInformation(
            "SectionsBuilt agent={AgentId} run={RunId} turn={TurnIndex} sections.count={SectionCount} budget.used={BudgetUsed:F4} budget.dropped={DroppedCount} budget.truncated={TruncatedCount} sections={SectionsJson}",
            snapshot.Context.AgentName ?? "_unknown",
            snapshot.Context.RunId ?? "_unknown",
            snapshot.TurnIndex,
            snapshot.Sections.Count,
            snapshot.Budget.UsedRatio,
            snapshot.Budget.DroppedCount,
            snapshot.Budget.TruncatedCount,
            BuildSectionsJson(snapshot.Sections));

        return ValueTask.CompletedTask;
    }

    private static string BuildSectionsJson(IReadOnlyList<SectionMeasurement> sections)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var section in sections)
            {
                writer.WriteStartObject();
                writer.WriteString("id", section.Id);
                writer.WriteString("kind", section.Kind.ToString());

                if (section.ProducerId is not null)
                {
                    writer.WriteString("producer", section.ProducerId);
                }

                writer.WriteNumber("chars", section.Chars);

                if (section.Tokens is int tokens)
                {
                    writer.WriteNumber("tokens", tokens);
                }

                writer.WriteNumber("ratio", Math.Round(section.Ratio, 4));
                writer.WriteString("outcome", section.Outcome);

                if (section.DroppedChars > 0)
                {
                    writer.WriteNumber("dropped_chars", section.DroppedChars);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
