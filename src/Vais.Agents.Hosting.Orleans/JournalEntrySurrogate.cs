// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Kind discriminator for <see cref="JournalEntrySurrogate"/>. Mirrors the closed
/// <see cref="JournalEntry"/> hierarchy in Abstractions; adding a new entry subtype
/// requires extending this enum and the converter in lock-step.
/// </summary>
public enum JournalEntryKind
{
    /// <summary><see cref="ToolCallRecorded"/>.</summary>
    ToolCallRecorded = 0,
}

/// <summary>
/// Orleans serialisation surrogate for the polymorphic <see cref="JournalEntry"/>
/// hierarchy. Flat shape with a discriminator + the union of all subclass fields;
/// the Abstractions package stays Orleans-free, so the
/// <see cref="GenerateSerializerAttribute"/>/<see cref="RegisterConverterAttribute"/>
/// pair lives here.
/// </summary>
[GenerateSerializer]
public struct JournalEntrySurrogate
{
    /// <summary>Discriminator — which concrete subtype this surrogate represents.</summary>
    [Id(0)]
    public JournalEntryKind Kind;

    /// <summary>Run this entry belongs to.</summary>
    [Id(1)]
    public string RunId;

    /// <summary>UTC timestamp when the entry was appended.</summary>
    [Id(2)]
    public DateTimeOffset At;

    /// <summary>Tool-call correlation id — populated for <see cref="JournalEntryKind.ToolCallRecorded"/>.</summary>
    [Id(3)]
    public string? CallId;

    /// <summary>Tool name — populated for <see cref="JournalEntryKind.ToolCallRecorded"/>.</summary>
    [Id(4)]
    public string? ToolName;

    /// <summary>
    /// Raw JSON text of the arguments — populated for <see cref="JournalEntryKind.ToolCallRecorded"/>.
    /// <see cref="JsonElement"/> isn't directly Orleans-serialisable so we round-trip through
    /// the JSON text representation (always valid because the original JsonElement came from a document).
    /// </summary>
    [Id(5)]
    public string? ArgumentsJson;

    /// <summary>Outcome call id — populated for <see cref="JournalEntryKind.ToolCallRecorded"/>.</summary>
    [Id(6)]
    public string? OutcomeCallId;

    /// <summary>Outcome result string — populated for <see cref="JournalEntryKind.ToolCallRecorded"/>.</summary>
    [Id(7)]
    public string? OutcomeResult;

    /// <summary>Outcome error type name — populated for <see cref="JournalEntryKind.ToolCallRecorded"/> when the tool threw.</summary>
    [Id(8)]
    public string? OutcomeError;
}

/// <summary>
/// Shared conversion helpers between <see cref="JournalEntry"/> and
/// <see cref="JournalEntrySurrogate"/>. Used by the per-subclass converters — Orleans'
/// <see cref="IConverter{TValue, TSurrogate}"/> resolves by exact <c>TValue</c>, so
/// the abstract base plus every concrete subtype each need their own converter entry.
/// </summary>
internal static class JournalEntrySurrogateHelpers
{
    public static JournalEntry FromSurrogate(in JournalEntrySurrogate surrogate)
    {
        return surrogate.Kind switch
        {
            JournalEntryKind.ToolCallRecorded => new ToolCallRecorded(
                RunId: surrogate.RunId,
                CallId: surrogate.CallId ?? string.Empty,
                ToolName: surrogate.ToolName ?? string.Empty,
                Arguments: ParseJson(surrogate.ArgumentsJson),
                Outcome: new ToolCallOutcome(
                    surrogate.OutcomeCallId ?? surrogate.CallId ?? string.Empty,
                    surrogate.OutcomeResult ?? string.Empty,
                    surrogate.OutcomeError),
                At: surrogate.At),
            _ => throw new NotSupportedException($"Unknown JournalEntryKind: {surrogate.Kind}"),
        };
    }

    public static JournalEntrySurrogate ToSurrogate(in JournalEntry value)
    {
        return value switch
        {
            ToolCallRecorded t => new JournalEntrySurrogate
            {
                Kind = JournalEntryKind.ToolCallRecorded,
                RunId = t.RunId,
                At = t.At,
                CallId = t.CallId,
                ToolName = t.ToolName,
                ArgumentsJson = SerializeJson(t.Arguments),
                OutcomeCallId = t.Outcome.CallId,
                OutcomeResult = t.Outcome.Result,
                OutcomeError = t.Outcome.Error,
            },
            _ => throw new NotSupportedException($"Unknown JournalEntry subtype: {value.GetType().Name}"),
        };
    }

    private static JsonElement ParseJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return JsonDocument.Parse("{}").RootElement;
        }
        return JsonDocument.Parse(raw).RootElement;
    }

    private static string SerializeJson(JsonElement value) => value.GetRawText();
}

/// <summary>
/// Converter for the abstract <see cref="JournalEntry"/> base type. Orleans uses exact-type
/// dispatch for <see cref="IConverter{TValue, TSurrogate}"/>, so polymorphic sites that pass
/// entries as <see cref="JournalEntry"/> (e.g. the grain state's <c>List&lt;JournalEntry&gt;</c>)
/// resolve through this one.
/// </summary>
[RegisterConverter]
public sealed class JournalEntrySurrogateConverter : IConverter<JournalEntry, JournalEntrySurrogate>
{
    /// <inheritdoc />
    public JournalEntry ConvertFromSurrogate(in JournalEntrySurrogate surrogate)
        => JournalEntrySurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public JournalEntrySurrogate ConvertToSurrogate(in JournalEntry value)
        => JournalEntrySurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="ToolCallRecorded"/>. Needed alongside the base-type converter because Orleans resolves by exact runtime type.</summary>
[RegisterConverter]
public sealed class ToolCallRecordedSurrogateConverter : IConverter<ToolCallRecorded, JournalEntrySurrogate>
{
    /// <inheritdoc />
    public ToolCallRecorded ConvertFromSurrogate(in JournalEntrySurrogate surrogate)
        => (ToolCallRecorded)JournalEntrySurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public JournalEntrySurrogate ConvertToSurrogate(in ToolCallRecorded value)
        => JournalEntrySurrogateHelpers.ToSurrogate(value);
}
