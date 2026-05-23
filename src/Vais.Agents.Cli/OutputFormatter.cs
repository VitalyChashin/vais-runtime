// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Vais.Agents.Control.Manifests;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vais.Agents.Cli;

/// <summary>
/// Output-format values consumers pass to <c>-o</c> / <c>--output</c>.
/// </summary>
internal enum OutputFormat
{
    Table = 0,
    Yaml = 1,
    Json = 2,
    JUnit = 3,
}

/// <summary>
/// Central dispatch for rendering verb results. Table via Spectre's
/// <see cref="IAnsiConsole"/>, JSON via <see cref="JsonSerializer"/>
/// with <see cref="JsonSerializerDefaults.Web"/>, YAML via
/// <see cref="YamlDotNet"/> with camelCase naming.
/// </summary>
internal static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    // Re-appliable envelope YAML: quote strings that would otherwise re-parse as a
    // different scalar type (e.g. version "1.0" → float, "true" → bool), so the emitted
    // YAML round-trips back to the same JSON the loader expects.
    private static readonly ISerializer EnvelopeYamlSerializer = new SerializerBuilder()
        .WithQuotingNecessaryStrings()
        .Build();

    /// <summary>
    /// Parse the <c>-o</c> / <c>--output</c> flag into a typed
    /// <see cref="OutputFormat"/>. Null / empty / unrecognised →
    /// <paramref name="fallback"/>.
    /// </summary>
    public static OutputFormat Parse(string? flag, OutputFormat fallback)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return fallback;
        }
        return flag.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "yaml" or "yml" => OutputFormat.Yaml,
            "table" => OutputFormat.Table,
            "junit" => OutputFormat.JUnit,
            _ => fallback,
        };
    }

    /// <summary>Render <paramref name="value"/> as pretty-printed JSON to <paramref name="console"/>.</summary>
    public static void WriteJson<T>(T value, IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        console.WriteLine(json);
    }

    /// <summary>Render <paramref name="value"/> as camelCase YAML to <paramref name="console"/>.</summary>
    public static void WriteYaml<T>(T value, IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var yaml = YamlSerializer.Serialize(value);
        console.WriteLine(yaml);
    }

    /// <summary>
    /// Render <paramref name="manifest"/> as a re-appliable v0.6 envelope (apiVersion / kind /
    /// metadata / spec) in <paramref name="format"/> — the shape <c>vais apply</c> consumes.
    /// </summary>
    public static void WriteManifestEnvelope<T>(T manifest, string kind, OutputFormat format, IAnsiConsole console)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(console);
        var envelope = EnvelopeCodec.Serialize(manifest, kind);
        console.WriteLine(format == OutputFormat.Json ? PrettyJson(envelope) : EnvelopeJsonToYaml(envelope));
    }

    /// <summary>
    /// Render <paramref name="manifests"/> as a re-appliable collection of envelopes — a JSON
    /// array (<c>-o json</c>) or a YAML sequence (<c>-o yaml</c>), both accepted by <c>vais apply</c>.
    /// </summary>
    public static void WriteManifestEnvelopeList<T>(IReadOnlyList<T> manifests, string kind, OutputFormat format, IAnsiConsole console)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(console);
        var array = new JsonArray();
        foreach (var manifest in manifests)
            array.Add(JsonNode.Parse(EnvelopeCodec.Serialize(manifest, kind)));
        var json = array.ToJsonString();
        console.WriteLine(format == OutputFormat.Json ? PrettyJson(json) : EnvelopeJsonToYaml(json));
    }

    private static string PrettyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
    }

    // JSON → YAML preserving scalar types (numbers/bools stay unquoted so the YAML
    // re-parses to the same JSON the loader expects). A naive YamlDotNet round-trip
    // would coerce scalars to strings.
    private static string EnvelopeJsonToYaml(string json)
    {
        var node = JsonNode.Parse(json);
        return EnvelopeYamlSerializer.Serialize(ToYamlObject(node));
    }

    private static object? ToYamlObject(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(kv => kv.Key, kv => ToYamlObject(kv.Value)),
        JsonArray arr => arr.Select(ToYamlObject).ToList(),
        JsonValue value => ToScalar(value),
        _ => node.ToJsonString(),
    };

    private static object? ToScalar(JsonValue value)
    {
        var element = value.GetValue<JsonElement>();
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }
}
