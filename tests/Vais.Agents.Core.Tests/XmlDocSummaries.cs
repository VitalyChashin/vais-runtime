// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Loads <c>&lt;summary&gt;</c> text from the compiled XML doc file, keyed by XML-doc
/// member id (<c>P:Namespace.Type.Property</c>). Feeds <c>ManifestJsonSchemaGenerator</c>'s
/// optional descriptions so the generated schemas/docs carry field descriptions — single-
/// sourced from the records' doc comments (the compiler emits a <c>P:</c> summary for every
/// property, positional record params included).
/// </summary>
internal static class XmlDocSummaries
{
    public static IReadOnlyDictionary<string, string> ForAbstractions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vais.Agents.Abstractions.xml");
        var doc = XDocument.Load(path);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in doc.Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null) continue;
            var text = Clean(summary);
            if (text.Length > 0) map[name] = text;
        }
        return map;
    }

    private static string Clean(XElement summary)
    {
        var sb = new StringBuilder();
        AppendNodes(summary, sb);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static void AppendNodes(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see":
                        case "seealso":
                            sb.Append(LastSegment(child.Attribute("cref")?.Value ?? child.Attribute("langword")?.Value));
                            break;
                        case "paramref":
                        case "typeparamref":
                            sb.Append(child.Attribute("name")?.Value);
                            break;
                        default:
                            AppendNodes(child, sb); // c, code, para, list, … → inner text
                            break;
                    }
                    break;
            }
        }
    }

    // "T:Vais.Agents.GraphEdgePredicate" -> "GraphEdgePredicate"; "P:...AgentGraphManifest.Nodes" -> "Nodes".
    private static string LastSegment(string? cref)
    {
        if (string.IsNullOrEmpty(cref)) return "";
        var s = cref.Contains(':') ? cref[(cref.IndexOf(':') + 1)..] : cref;
        var dot = s.LastIndexOf('.');
        var seg = dot >= 0 ? s[(dot + 1)..] : s;
        var arity = seg.IndexOf('`');
        return arity >= 0 ? seg[..arity] : seg;
    }
}
