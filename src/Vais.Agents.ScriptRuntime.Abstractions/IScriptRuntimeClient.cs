// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// Runtime-side client for the ScriptRuntime sidecar. The <c>run_code</c> tool calls this to
/// execute an LLM-authored script; the implementation owns transport (HTTP) to the supervised
/// sidecar. Kept as an interface so the agent runtime depends on the seam, not the transport.
/// </summary>
public interface IScriptRuntimeClient
{
    /// <summary>Execute <paramref name="request"/> on the sidecar and return its result (or a classified error).</summary>
    Task<ScriptRunResponse> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken = default);
}
