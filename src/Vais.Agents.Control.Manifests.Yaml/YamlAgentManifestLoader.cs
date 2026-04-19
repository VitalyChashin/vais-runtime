// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using YamlDotNet.RepresentationModel;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// <see cref="IAgentManifestLoader"/> that reads YAML input — either a single
/// document or a multi-document stream using <c>---</c> separators. Parses via
/// YamlDotNet, normalises to JSON (preserving key order), and delegates to the
/// shared <see cref="JsonAgentManifestLoader"/> validation core. One format,
/// two wire shapes, one validator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key-order preservation.</b> YamlDotNet's <see cref="YamlStream"/> emits
/// <see cref="YamlMappingNode"/>s that preserve insertion order, and the
/// normaliser below emits JSON object properties in the same order. This is
/// load-bearing for SGR reasoning schemas — field order is part of the contract.
/// </para>
/// </remarks>
public sealed class YamlAgentManifestLoader : IAgentManifestLoader
{
    private readonly JsonAgentManifestLoader _inner = new();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentManifest>> LoadFromStringAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var json = YamlToJson(content);
        return _inner.LoadFromStringAsync(json, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentManifest>> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await LoadFromStringAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentManifest>> LoadFromDirectoryAsync(string directory, string searchPattern, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var all = new List<AgentManifest>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            try
            {
                var json = YamlToJson(content);
                var items = await _inner.LoadFromStringAsync(json, cancellationToken).ConfigureAwait(false);
                all.AddRange(items);
            }
            catch (AgentManifestValidationException ex)
            {
                foreach (var e in ex.Errors)
                {
                    errors.Add($"{Path.GetFileName(file)}: {e}");
                }
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                errors.Add($"{Path.GetFileName(file)}: YAML parse error — {ex.Message}");
            }
        }

        // Cross-file duplicate-id check.
        var seen = new HashSet<(string Id, string Version)>();
        foreach (var m in all)
        {
            var key = (m.Id, m.Version);
            if (!seen.Add(key))
            {
                errors.Add($"duplicate manifest across files: id='{m.Id}' version='{m.Version}'");
            }
        }

        if (errors.Count > 0)
        {
            throw new AgentManifestValidationException(errors);
        }
        return all;
    }

    /// <summary>Normalise a YAML source (single document or multi-doc stream) into a JSON text.</summary>
    internal static string YamlToJson(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0)
        {
            return "null";
        }

        if (stream.Documents.Count == 1)
        {
            var sb = new StringBuilder();
            EmitNode(stream.Documents[0].RootNode, sb);
            return sb.ToString();
        }

        // Multi-document YAML → JSON array of documents.
        var arr = new StringBuilder();
        arr.Append('[');
        for (var i = 0; i < stream.Documents.Count; i++)
        {
            if (i > 0) arr.Append(',');
            EmitNode(stream.Documents[i].RootNode, arr);
        }
        arr.Append(']');
        return arr.ToString();
    }

    private static void EmitNode(YamlNode node, StringBuilder sb)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                EmitScalar(scalar, sb);
                return;
            case YamlSequenceNode seq:
                sb.Append('[');
                var first = true;
                foreach (var child in seq.Children)
                {
                    if (!first) sb.Append(',');
                    EmitNode(child, sb);
                    first = false;
                }
                sb.Append(']');
                return;
            case YamlMappingNode map:
                sb.Append('{');
                var firstKey = true;
                foreach (var kv in map.Children)
                {
                    if (!firstKey) sb.Append(',');
                    var key = kv.Key is YamlScalarNode ks ? ks.Value ?? string.Empty : kv.Key.ToString() ?? string.Empty;
                    AppendJsonString(sb, key);
                    sb.Append(':');
                    EmitNode(kv.Value, sb);
                    firstKey = false;
                }
                sb.Append('}');
                return;
            default:
                sb.Append("null");
                return;
        }
    }

    private static void EmitScalar(YamlScalarNode scalar, StringBuilder sb)
    {
        var value = scalar.Value;
        if (value is null)
        {
            sb.Append("null");
            return;
        }

        // YamlDotNet annotates explicit types on the node (tag/style). When the input
        // used quotes, the style is SingleQuoted / DoubleQuoted and we should emit a
        // JSON string regardless of content. Otherwise, resolve against YAML 1.2's
        // core types (null / bool / int / float / string).
        var style = scalar.Style;
        var isQuoted = style == YamlDotNet.Core.ScalarStyle.SingleQuoted
                    || style == YamlDotNet.Core.ScalarStyle.DoubleQuoted;

        if (!isQuoted)
        {
            if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("~", StringComparison.Ordinal) || value.Length == 0)
            {
                sb.Append("null");
                return;
            }
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("true");
                return;
            }
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("false");
                return;
            }
            if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i))
            {
                sb.Append(i);
                return;
            }
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                && !value.StartsWith(".", StringComparison.Ordinal)
                && !value.StartsWith("+", StringComparison.Ordinal))
            {
                sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
        }

        AppendJsonString(sb, value);
    }

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
