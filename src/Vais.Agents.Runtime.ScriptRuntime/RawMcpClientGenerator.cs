// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// Default tool-API generator: emits a flat <c>tools</c> object from the agent's resolved MCP
/// tool set. Each tool becomes a function keyed by its exact name; the body forwards to the
/// single <c>__callTool</c> host bridge. A leading comment block documents each tool (name,
/// description, top-level argument names) so the model can write code against the surface.
/// </summary>
internal sealed class RawMcpClientGenerator : IToolApiGenerator
{
    public string Generate(IReadOnlyList<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var sb = new StringBuilder();
        sb.Append("// Code-mode tool API. Call a function and it returns the tool's result synchronously.\n");
        sb.Append("// Pass arguments as a single object literal; omit it for no-arg tools.\n");
        if (tools.Count == 0)
        {
            sb.Append("// (this agent has no tools available)\n");
        }

        foreach (var tool in tools)
        {
            sb.Append("//   tools[").Append(JsString(tool.Name)).Append("](args)");
            var desc = OneLine(tool.Description);
            if (desc.Length > 0)
            {
                sb.Append(" — ").Append(desc);
            }

            var props = PropertyNames(tool.ParametersSchema);
            if (props.Count > 0)
            {
                sb.Append("  args: { ").Append(string.Join(", ", props)).Append(" }");
            }

            sb.Append('\n');
        }

        sb.Append("var tools = {\n");
        for (var i = 0; i < tools.Count; i++)
        {
            var name = tools[i].Name;
            sb.Append("  ").Append(JsString(name))
              .Append(": function (args) { return __callTool(\"\", ").Append(JsString(name))
              .Append(", JSON.stringify(args === undefined ? {} : args)); }")
              .Append(i < tools.Count - 1 ? ",\n" : "\n");
        }

        sb.Append("};\n");
        return sb.ToString();
    }

    private static IReadOnlyList<string> PropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var p in props.EnumerateObject())
        {
            names.Add(p.Name);
        }

        return names;
    }

    // Collapse newlines so a description can't break the single-line comment.
    private static string OneLine(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.ReplaceLineEndings(" ").Trim();

    // Minimal JS/JSON string literal: escape backslash and double-quote (tool names match
    // [A-Za-z0-9_-]+ so this is defensive, but the helper is reused for both contexts).
    private static string JsString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
