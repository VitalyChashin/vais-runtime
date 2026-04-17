// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Vais2.Agents.Ai.MicrosoftAgentFramework;

/// <summary>
/// Bridges <see cref="ITool"/> instances onto MAF's tool surface via the MEAI
/// <see cref="AIFunction"/> base class. MAF's <c>ChatClientAgent</c> consumes
/// <see cref="AITool"/> instances through <c>ChatOptions.Tools</c>; auto-invocation
/// happens inside the <see cref="FunctionInvokingChatClient"/> layer of the pipeline.
/// </summary>
internal static class MafToolBinder
{
    /// <summary>Adapt a neutral tool list into MEAI <see cref="AITool"/>s.</summary>
    public static IList<AITool> BuildTools(IReadOnlyList<ITool> tools) =>
        tools.Select(t => (AITool)new ToolAsAiFunction(t)).ToList();

    /// <summary>
    /// Thin adapter from <see cref="ITool"/> onto MEAI's <see cref="AIFunction"/>.
    /// Arguments are marshalled to and from JSON so the tool implementation stays
    /// free of MEAI types.
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
            var dict = arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>();
            var json = JsonSerializer.SerializeToElement(dict);
            return await _tool.InvokeAsync(json, cancellationToken).ConfigureAwait(false);
        }
    }
}
