// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Core;

/// <summary>
/// Default in-process <see cref="IToolCallDispatcher"/>. Resolves each call against
/// an <see cref="IToolRegistry"/>, runs registered <see cref="IToolGuardrail"/>s
/// before and after invocation, and wraps tool exceptions in a
/// <see cref="ToolCallOutcome"/> with a non-null <see cref="ToolCallOutcome.Error"/>
/// so the loop can feed the error back to the model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure vs. abort.</b> A <em>tool</em> throwing is a recoverable failure —
/// the outcome carries the formatted error and the loop continues. A <em>guardrail</em>
/// denial is non-recoverable — it throws <see cref="AgentGuardrailDeniedException"/>
/// which the loop catches and surfaces as a failed turn.
/// </para>
/// <para>
/// Event emission (<c>ToolCallStarted</c>, <c>ToolCallCompleted</c>) lands with the
/// Orleans surrogate regen in PR 9c. This dispatcher is silent in v0.4 PR 9a.
/// </para>
/// </remarks>
public sealed class DefaultToolCallDispatcher : IToolCallDispatcher
{
    private readonly IToolRegistry? _toolRegistry;
    private readonly IReadOnlyList<IToolGuardrail> _toolGuardrails;

    /// <summary>Construct a dispatcher over the given registry + guardrails.</summary>
    public DefaultToolCallDispatcher(IToolRegistry? toolRegistry, IReadOnlyList<IToolGuardrail>? toolGuardrails = null)
    {
        _toolRegistry = toolRegistry;
        _toolGuardrails = toolGuardrails ?? Array.Empty<IToolGuardrail>();
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallOutcome> DispatchAsync(
        ToolCallRequest request,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tool = _toolRegistry?.GetByName(request.ToolName);
        if (tool is null)
        {
            return new ToolCallOutcome(
                request.CallId,
                Result: $"Tool '{request.ToolName}' not found in registry.",
                Error: nameof(KeyNotFoundException));
        }

        foreach (var guardrail in _toolGuardrails)
        {
            var outcome = await guardrail.BeforeInvokeAsync(tool, request.Arguments, context, cancellationToken).ConfigureAwait(false);
            if (outcome.Decision == GuardrailDecision.Deny)
            {
                throw new AgentGuardrailDeniedException(GuardrailLayer.Tool, outcome.Reason);
            }
        }

        string result;
        try
        {
            result = await tool.InvokeAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolCallOutcome(
                request.CallId,
                Result: $"Tool '{request.ToolName}' failed: {ex.Message}",
                Error: ex.GetType().Name);
        }

        foreach (var guardrail in _toolGuardrails)
        {
            var outcome = await guardrail.AfterInvokeAsync(tool, request.Arguments, result, context, cancellationToken).ConfigureAwait(false);
            if (outcome.Decision == GuardrailDecision.Deny)
            {
                throw new AgentGuardrailDeniedException(GuardrailLayer.Tool, outcome.Reason);
            }
        }

        return new ToolCallOutcome(request.CallId, result);
    }
}
