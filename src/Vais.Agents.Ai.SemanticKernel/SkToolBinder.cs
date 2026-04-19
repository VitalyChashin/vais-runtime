// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Vais.Agents.Ai.SemanticKernel;

/// <summary>
/// Bridges <see cref="ITool"/> instances onto SK's <see cref="KernelFunction"/> surface
/// via the MEAI <see cref="AIFunction"/> intermediate — the smallest supported bridge
/// that keeps schemas and argument marshalling intact.
/// </summary>
internal static class SkToolBinder
{
    /// <summary>Build a <see cref="KernelPlugin"/> containing one function per registered tool.</summary>
    public static KernelPlugin BuildPlugin(IReadOnlyList<ITool> tools, string pluginName = "Tools")
    {
        var functions = tools.Select(t => new ToolAsAiFunction(t).AsKernelFunction()).ToArray();
        return KernelPluginFactory.CreateFromFunctions(pluginName, functions);
    }

    /// <summary>
    /// Adapter: <see cref="ITool"/> → <see cref="AIFunction"/>. MEAI's function surface
    /// is the common denominator SK and MAF both understand, so we go through it rather
    /// than write two separate translations.
    /// </summary>
    private sealed class ToolAsAiFunction : AIFunction
    {
        private readonly ITool _tool;

        public ToolAsAiFunction(ITool tool)
        {
            _tool = tool;
        }

        public override string Name => _tool.Name;
        public override string Description => _tool.Description;
        public override JsonElement JsonSchema => _tool.ParametersSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // MEAI hands us an `AIFunctionArguments` dictionary of model-produced args.
            // Collapse it back into a JsonElement for our neutral ITool contract so the
            // tool implementation doesn't have to depend on MEAI.
            var dict = arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>();
            var json = JsonSerializer.SerializeToElement(dict);
            return await _tool.InvokeAsync(json, cancellationToken).ConfigureAwait(false);
        }
    }
}
