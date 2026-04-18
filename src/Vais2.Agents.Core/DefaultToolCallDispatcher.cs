// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais2.Agents.Core;

/// <summary>
/// Default in-process <see cref="IToolCallDispatcher"/>. Resolves each call against
/// an <see cref="IToolRegistry"/>, runs registered <see cref="IToolGuardrail"/>s
/// before and after invocation, wraps tool exceptions in a
/// <see cref="ToolCallOutcome"/> with a non-null <see cref="ToolCallOutcome.Error"/>
/// so the loop can feed the error back to the model, and emits
/// <see cref="ToolCallStarted"/> / <see cref="ToolCallCompleted"/> events on the
/// injected bus when one is supplied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure vs. abort.</b> A <em>tool</em> throwing is a recoverable failure —
/// the outcome carries the formatted error and the loop continues. A <em>guardrail</em>
/// denial is non-recoverable — it throws <see cref="AgentGuardrailDeniedException"/>
/// which the loop catches and surfaces as a failed turn.
/// </para>
/// </remarks>
public sealed class DefaultToolCallDispatcher : IToolCallDispatcher
{
    private readonly IToolRegistry? _toolRegistry;
    private readonly IReadOnlyList<IToolGuardrail> _toolGuardrails;
    private readonly IAgentEventBus _eventBus;

    /// <summary>Construct a dispatcher over the given registry + guardrails + optional event bus.</summary>
    public DefaultToolCallDispatcher(
        IToolRegistry? toolRegistry,
        IReadOnlyList<IToolGuardrail>? toolGuardrails = null,
        IAgentEventBus? eventBus = null)
    {
        _toolRegistry = toolRegistry;
        _toolGuardrails = toolGuardrails ?? Array.Empty<IToolGuardrail>();
        _eventBus = eventBus ?? NullAgentEventBus.Instance;
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
            await HandleToolGuardrailOutcomeAsync(outcome, context, cancellationToken).ConfigureAwait(false);
        }

        await _eventBus.PublishAsync(
            new ToolCallStarted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName),
            cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        string result;
        string? toolError = null;
        try
        {
            result = await tool.InvokeAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            toolError = ex.GetType().Name;
            await _eventBus.PublishAsync(
                new ToolCallCompleted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName, Succeeded: false, Error: toolError, sw.Elapsed),
                cancellationToken).ConfigureAwait(false);
            return new ToolCallOutcome(
                request.CallId,
                Result: $"Tool '{request.ToolName}' failed: {ex.Message}",
                Error: toolError);
        }
        sw.Stop();

        foreach (var guardrail in _toolGuardrails)
        {
            var outcome = await guardrail.AfterInvokeAsync(tool, request.Arguments, result, context, cancellationToken).ConfigureAwait(false);
            if (outcome.Decision != GuardrailDecision.Pass)
            {
                // Emit the completion event first so observers see a paired Started/Completed
                // even when the outer exception aborts the turn, then the guardrail/interrupt event.
                await _eventBus.PublishAsync(
                    new ToolCallCompleted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName, Succeeded: true, Error: null, sw.Elapsed),
                    cancellationToken).ConfigureAwait(false);
                await HandleToolGuardrailOutcomeAsync(outcome, context, cancellationToken).ConfigureAwait(false);
            }
        }

        await _eventBus.PublishAsync(
            new ToolCallCompleted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName, Succeeded: true, Error: null, sw.Elapsed),
            cancellationToken).ConfigureAwait(false);

        return new ToolCallOutcome(request.CallId, result);
    }

    private async Task HandleToolGuardrailOutcomeAsync(
        GuardrailOutcome outcome,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        switch (outcome.Decision)
        {
            case GuardrailDecision.Pass:
                return;
            case GuardrailDecision.Deny:
                await _eventBus.PublishAsync(
                    new GuardrailTriggered(DateTimeOffset.UtcNow, context, GuardrailLayer.Tool, outcome.Decision, outcome.Reason),
                    cancellationToken).ConfigureAwait(false);
                throw new AgentGuardrailDeniedException(GuardrailLayer.Tool, outcome.Reason);
            case GuardrailDecision.Interrupt:
                if (outcome.InterruptPayload is null)
                {
                    throw new InvalidOperationException(
                        "Tool guardrail returned Interrupt without an AgentInterrupt payload. " +
                        "Use GuardrailOutcome.Interrupt(AgentInterrupt, reason?) to construct this outcome.");
                }
                await _eventBus.PublishAsync(
                    new InterruptRaised(DateTimeOffset.UtcNow, context, outcome.InterruptPayload.InterruptId, outcome.InterruptPayload.Reason),
                    cancellationToken).ConfigureAwait(false);
                throw new AgentInterruptedException(outcome.InterruptPayload);
        }
    }
}
