// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary><see cref="CompletionDeltaRecorded"/>.</summary>
    CompletionDeltaRecorded = 1,
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

    /// <summary>Monotonic delta index within the run (0, 1, 2, ...) — populated for <see cref="JournalEntryKind.CompletionDeltaRecorded"/>.</summary>
    [Id(9)]
    public int SequenceNumber;

    /// <summary>Delta text fragment — populated for <see cref="JournalEntryKind.CompletionDeltaRecorded"/>.</summary>
    [Id(10)]
    public string TextDelta;

    /// <summary>Model id — populated for <see cref="JournalEntryKind.CompletionDeltaRecorded"/>.</summary>
    [Id(11)]
    public string? ModelId;

    /// <summary>Prompt-side token count — populated for <see cref="JournalEntryKind.CompletionDeltaRecorded"/>.</summary>
    [Id(12)]
    public int? PromptTokens;

    /// <summary>Completion-side token count — populated for <see cref="JournalEntryKind.CompletionDeltaRecorded"/>.</summary>
    [Id(13)]
    public int? CompletionTokens;

    /// <summary>Tool calls JSON — populated for <see cref="JournalEntryKind.CompletionDeltaRecorded"/>.</summary>
    [Id(14)]
    public string? ToolCallsJson;
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
            JournalEntryKind.CompletionDeltaRecorded => new CompletionDeltaRecorded(
                RunId: surrogate.RunId,
                SequenceNumber: surrogate.SequenceNumber,
                Delta: new CompletionUpdate(
                    TextDelta: surrogate.TextDelta,
                    ModelId: surrogate.ModelId,
                    PromptTokens: surrogate.PromptTokens,
                    CompletionTokens: surrogate.CompletionTokens,
                    ToolCalls: ParseToolCalls(surrogate.ToolCallsJson)),
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
            CompletionDeltaRecorded d => new JournalEntrySurrogate
            {
                Kind = JournalEntryKind.CompletionDeltaRecorded,
                RunId = d.RunId,
                At = d.At,
                SequenceNumber = d.SequenceNumber,
                TextDelta = d.Delta.TextDelta,
                ModelId = d.Delta.ModelId,
                PromptTokens = d.Delta.PromptTokens,
                CompletionTokens = d.Delta.CompletionTokens,
                ToolCallsJson = SerializeToolCalls(d.Delta.ToolCalls),
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

    private static IReadOnlyList<ToolCallRequest>? ParseToolCalls(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        var elements = JsonDocument.Parse(json).RootElement;
        if (elements.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var results = new List<ToolCallRequest>();
        foreach (var element in elements.EnumerateArray())
        {
            results.Add(new ToolCallRequest(
                ToolName: element.GetProperty("ToolName").GetString() ?? string.Empty,
                Arguments: element.GetProperty("Arguments"),
                CallId: element.GetProperty("CallId").GetString() ?? string.Empty));
        }
        return results;
    }

    private static string? SerializeToolCalls(IReadOnlyList<ToolCallRequest>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return null;
        }
        var array = new List<JsonNode>();
        foreach (var call in toolCalls)
        {
            var obj = new JsonObject
            {
                ["ToolName"] = call.ToolName,
                ["Arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                ["CallId"] = call.CallId
            };
            array.Add(obj);
        }
        return JsonSerializer.Serialize(array);
    }
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

/// <summary>Converter for concrete <see cref="CompletionDeltaRecorded"/>. Needed alongside the base-type converter because Orleans resolves by exact runtime type.</summary>
[RegisterConverter]
public sealed class CompletionDeltaRecordedSurrogateConverter : IConverter<CompletionDeltaRecorded, JournalEntrySurrogate>
{
    /// <inheritdoc />
    public CompletionDeltaRecorded ConvertFromSurrogate(in JournalEntrySurrogate surrogate)
        => (CompletionDeltaRecorded)JournalEntrySurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public JournalEntrySurrogate ConvertToSurrogate(in CompletionDeltaRecorded value)
        => JournalEntrySurrogateHelpers.ToSurrogate(value);
}
