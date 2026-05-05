// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.PowerFx;
using Microsoft.PowerFx.Types;

namespace Vais.Agents.Core.PowerFx;

/// <summary>
/// Evaluates PowerFx boolean expressions against graph state.
/// State keys are exposed under the <c>Local</c> namespace: <c>Local.key_name</c>.
/// Hyphens in key names are normalised to underscores (<c>research-plan</c> → <c>Local.research_plan</c>).
/// The well-known <c>Local.lastMessage</c> shortcut exposes the last entry of the
/// <c>messages</c> array as a record (<c>Local.lastMessage.text</c>, <c>Local.lastMessage.role</c>).
/// </summary>
public sealed class PowerFxGraphExpressionEvaluator : IGraphExpressionEvaluator
{
    // RecalcEngine is thread-safe for concurrent EvalAsync calls with separate RuntimeConfigs.
    private static readonly RecalcEngine _engine = new();

    /// <inheritdoc />
    public async ValueTask<bool> EvaluatePredicateAsync(
        string expression,
        IReadOnlyDictionary<string, JsonElement> state,
        CancellationToken cancellationToken = default)
    {
        var expr = expression.StartsWith('=') ? expression[1..] : expression;
        var record = BuildStateRecord(state);
        var result = await _engine.EvalAsync(expr, cancellationToken, record)
            .ConfigureAwait(false);

        return result switch
        {
            BooleanValue b => b.Value,
            ErrorValue e => throw new InvalidOperationException(
                $"PowerFx expression '{expression}' evaluation error: {string.Join(", ", e.Errors.Select(x => x.Message))}"),
            _ => throw new InvalidOperationException(
                $"PowerFx expression '{expression}' returned a non-boolean value ({result?.GetType().Name ?? "null"})."),
        };
    }

    private static RecordValue BuildStateRecord(IReadOnlyDictionary<string, JsonElement> state)
    {
        IEnumerable<NamedValue> localFields = state.Select(kvp =>
            new NamedValue(SanitizeIdentifier(kvp.Key), JsonElementToFormulaValue(kvp.Value)));

        // Expose lastMessage as a computed shortcut inside Local.
        if (state.TryGetValue("messages", out var msgs)
            && msgs.ValueKind == JsonValueKind.Array
            && msgs.GetArrayLength() > 0)
        {
            var last = msgs[msgs.GetArrayLength() - 1];
            if (last.ValueKind == JsonValueKind.Object)
            {
                localFields = localFields.Append(new NamedValue("lastMessage", JsonObjectToRecord(last)));
            }
        }

        var localRecord = RecordValue.NewRecordFromFields(localFields);
        return RecordValue.NewRecordFromFields(new NamedValue("Local", localRecord));
    }

    private static FormulaValue JsonElementToFormulaValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => FormulaValue.New(el.GetString() ?? string.Empty),
        JsonValueKind.Number => FormulaValue.New(el.GetDouble()),
        JsonValueKind.True => FormulaValue.New(true),
        JsonValueKind.False => FormulaValue.New(false),
        JsonValueKind.Null => FormulaValue.NewBlank(FormulaType.String),
        JsonValueKind.Object => JsonObjectToRecord(el),
        // Arrays exposed as blank in v1; full TableValue support deferred.
        _ => FormulaValue.NewBlank(FormulaType.String),
    };

    private static RecordValue JsonObjectToRecord(JsonElement el)
    {
        var fields = el.EnumerateObject()
            .Select(p => new NamedValue(SanitizeIdentifier(p.Name), JsonElementToFormulaValue(p.Value)));
        return RecordValue.NewRecordFromFields(fields);
    }

    private static string SanitizeIdentifier(string key)
        => key.Replace('-', '_').Replace(' ', '_');
}
