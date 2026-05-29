// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Builds the single <c>run_code</c> tool that replaces an agent's normal tool set when
/// <see cref="CodeModeSpec.Enabled"/> is <c>true</c>. Implemented by the ScriptRuntime package
/// and resolved by the manifest translator at instantiation time; absent when the runtime host
/// has not enabled code-mode (the translator then rejects a code-mode manifest with a clear error).
/// </summary>
public interface ICodeModeToolFactory
{
    /// <summary>
    /// Create the LLM-facing <c>run_code</c> tool for <paramref name="agentId"/> over the agent's
    /// resolved <paramref name="tools"/> (optionally narrowed by <see cref="CodeModeSpec.Toolset"/>).
    /// </summary>
    ITool Create(string agentId, CodeModeSpec spec, IReadOnlyList<ITool> tools);
}
