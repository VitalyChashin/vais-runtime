// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when the agent response can be parsed as JSON and (when the schema
/// specifies required fields) all required properties are present.
/// Config: <c>{ "schema": { "type": "object", "required": ["name", "age"] } }</c>.
/// </summary>
internal sealed class ResponseJsonSchemaAssertion : IEvalAssertion
{
    private readonly JsonElement _schema;

    /// <summary>Construct with a JSON schema element.</summary>
    public ResponseJsonSchemaAssertion(JsonElement schema) => _schema = schema;

    /// <inheritdoc/>
    public string Kind => "response-json-schema";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        // Prefer the pre-parsed ResponseJson; fall back to parsing ResponseText.
        JsonElement doc;
        try
        {
            if (run.ResponseJson.HasValue && run.ResponseJson.Value.ValueKind != JsonValueKind.Null)
                doc = run.ResponseJson.Value;
            else
                doc = JsonSerializer.Deserialize<JsonElement>(run.ResponseText);
        }
        catch (JsonException ex)
        {
            return ValueTask.FromResult(new EvalAssertionResult(
                EvalAssertionStatus.Fail,
                Score: 0.0,
                Reason: $"Response is not valid JSON: {ex.Message}"));
        }

        // Root type check.
        if (_schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var expectedType = typeEl.GetString();
            if (!IsJsonType(doc, expectedType!))
            {
                return ValueTask.FromResult(new EvalAssertionResult(
                    EvalAssertionStatus.Fail,
                    Score: 0.0,
                    Reason: $"Expected JSON type '{expectedType}' but got '{JsonKindToTypeName(doc.ValueKind)}'"));
            }
        }

        // Required-properties check (only meaningful when root is an object).
        if (_schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            var missing = new List<string>();
            foreach (var req in reqEl.EnumerateArray())
            {
                var name = req.GetString();
                if (name is not null && (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty(name, out _)))
                    missing.Add(name);
            }

            if (missing.Count > 0)
            {
                return ValueTask.FromResult(new EvalAssertionResult(
                    EvalAssertionStatus.Fail,
                    Score: 0.0,
                    Reason: $"Missing required properties: {string.Join(", ", missing)}"));
            }
        }

        return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));
    }

    private static bool IsJsonType(JsonElement el, string typeName) => typeName switch
    {
        "object" => el.ValueKind == JsonValueKind.Object,
        "array"  => el.ValueKind == JsonValueKind.Array,
        "string" => el.ValueKind == JsonValueKind.String,
        "number" => el.ValueKind is JsonValueKind.Number,
        "boolean" => el.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null"   => el.ValueKind == JsonValueKind.Null,
        _        => true, // unknown type name — skip type check
    };

    private static string JsonKindToTypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array  => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null   => "null",
        _                    => kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>Factory for <see cref="ResponseJsonSchemaAssertion"/>.</summary>
internal sealed class ResponseJsonSchemaAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "response-json-schema";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        JsonElement schema = default;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("schema", out var s))
            schema = s;
        return new ResponseJsonSchemaAssertion(schema);
    }
}
