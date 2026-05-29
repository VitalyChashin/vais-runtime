// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// Generates the JS prelude — the <c>tools</c> object the LLM writes code against — from an
/// agent's resolved tool set. Pluggable so the surface can be raw-MCP-derived (v1) or
/// ontology-derived (deferred): the implementation chooses how tool schemas become callable
/// functions, but every generated call must route through the single <c>__callTool</c> bridge.
/// </summary>
public interface IToolApiGenerator
{
    /// <summary>Build the JS prelude defining <c>tools.*</c> for <paramref name="tools"/>.</summary>
    string Generate(IReadOnlyList<ITool> tools);
}
