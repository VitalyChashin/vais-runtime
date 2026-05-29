// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.ScriptRuntime.Tests;

/// <summary>Minimal <see cref="ITool"/> for generator/factory tests.</summary>
internal sealed class FakeTool(string name, string description = "", string? schemaJson = null) : ITool
{
    private readonly JsonElement _schema = Parse(schemaJson);

    public string Name => name;
    public string Description => description;
    public JsonElement ParametersSchema => _schema;

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    private static JsonElement Parse(string? json)
    {
        using var doc = JsonDocument.Parse(json ?? "{\"type\":\"object\"}");
        return doc.RootElement.Clone();
    }
}
