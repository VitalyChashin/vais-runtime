// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

/// <summary>
/// Converts between a typed <c>TState</c> POCO and the runtime bag
/// form (<see cref="IDictionary{TKey,TValue}"/> of <see cref="JsonElement"/>). Mirrors
/// the logic in <c>InProcessGraphOrchestrator</c>'s internal bag converter so the
/// MAF adapter's typed-state semantics match the in-process orchestrator's exactly.
/// </summary>
internal static class StateBagConverter
{
    public static IDictionary<string, JsonElement> ToBag<TState>(TState initial)
    {
        if (initial is IDictionary<string, JsonElement> already)
        {
            return new Dictionary<string, JsonElement>(already, StringComparer.Ordinal);
        }
        if (initial is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        var json = JsonSerializer.SerializeToElement(initial);
        var bag = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in json.EnumerateObject())
            {
                bag[prop.Name] = prop.Value;
            }
        }
        return bag;
    }

    public static TState FromBag<TState>(IDictionary<string, JsonElement> bag)
    {
        if (typeof(TState) == typeof(IDictionary<string, JsonElement>))
        {
            return (TState)(object)bag;
        }
        var json = JsonSerializer.SerializeToElement(bag);
        return JsonSerializer.Deserialize<TState>(json)!;
    }
}
