// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// Builds the <c>run_code</c> tool for a code-mode agent: generates the JS API surface over the
/// agent's tools (optionally narrowed by <see cref="CodeModeSpec.Toolset"/>) and wires the sidecar
/// client + call-token service into a <see cref="RunCodeTool"/>.
/// </summary>
internal sealed class CodeModeToolFactory(
    IToolApiGenerator generator,
    IScriptRuntimeClient client,
    ICallTokenService callTokens,
    ScriptRuntimeOptions options,
    ILoggerFactory loggerFactory) : ICodeModeToolFactory
{
    public ITool Create(string agentId, CodeModeSpec spec, IReadOnlyList<ITool> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tools);

        var selected = SelectTools(spec, tools);
        var prelude = generator.Generate(selected);
        return new RunCodeTool(
            agentId,
            prelude,
            spec.Limits ?? new CodeModeLimits(),
            client,
            callTokens,
            options,
            loggerFactory.CreateLogger<RunCodeTool>());
    }

    private static IReadOnlyList<ITool> SelectTools(CodeModeSpec spec, IReadOnlyList<ITool> tools)
    {
        if (spec.Toolset is not { Count: > 0 } names)
        {
            return tools;
        }

        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        return tools.Where(t => allowed.Contains(t.Name)).ToList();
    }
}
