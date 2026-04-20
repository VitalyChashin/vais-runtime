// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Spectre.Console;
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
}
