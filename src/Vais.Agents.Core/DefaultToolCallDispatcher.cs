// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Core;

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
/// <para>
/// <b>Durable execution (v0.5+).</b> When an <see cref="IAgentJournal"/> is supplied
/// and the ambient <see cref="AgentContext.RunId"/> is set, every produced
/// <see cref="ToolCallOutcome"/> (success + tool-exception alike) is appended to
/// the journal as a <see cref="ToolCallRecorded"/> entry. Before invoking a tool
/// the dispatcher checks the journal for a prior outcome under the same
/// (<see cref="AgentContext.RunId"/>, <see cref="ToolCallRequest.CallId"/>) pair
/// and, on hit, returns the recorded outcome directly — skipping the tool, the
/// guardrails, and event emission. Cache replay is deliberately silent on the
/// event bus in v0.5 PR 2; a dedicated <c>ToolCallReplayed</c> event lands with
/// the resume semantics in a follow-up PR. Guardrail denials and interrupts are
/// <em>not</em> journaled: they throw instead of returning an outcome, and their
/// replay semantics are handled when the agent-level resume logic lands.
/// </para>
/// </remarks>
public sealed class DefaultToolCallDispatcher : IToolCallDispatcher
{
    private readonly IToolRegistry? _toolRegistry;
    private readonly IReadOnlyList<IToolGuardrail> _toolGuardrails;
    private readonly IAgentEventBus _eventBus;
    private readonly IAgentJournal _journal;
    private readonly ToolGatewayMiddleware[] _gatewayMiddleware;
    private readonly IAgentContextSetter? _contextSetter;

    /// <summary>
    /// Construct a dispatcher over the given registry + guardrails + optional event bus + optional journal.
    /// When <paramref name="contextSetter"/> is supplied, the run-stamped <see cref="AgentContext"/> is
    /// pushed onto the ambient accessor for the duration of each tool invocation, so tools that read
    /// <c>IAgentContextAccessor.Current</c> observe the current <see cref="AgentContext.RunId"/> + identity
    /// rather than only the framework's explicit context parameter.
    /// </summary>
    public DefaultToolCallDispatcher(
        IToolRegistry? toolRegistry,
        IReadOnlyList<IToolGuardrail>? toolGuardrails = null,
        IAgentEventBus? eventBus = null,
        IAgentJournal? journal = null,
        IEnumerable<ToolGatewayMiddleware>? gatewayMiddleware = null,
        IAgentContextSetter? contextSetter = null)
    {
        _toolRegistry = toolRegistry;
        _toolGuardrails = toolGuardrails ?? Array.Empty<IToolGuardrail>();
        _eventBus = eventBus ?? NullAgentEventBus.Instance;
        _journal = journal ?? NullAgentJournal.Instance;
        _gatewayMiddleware = gatewayMiddleware?.ToArray() ?? Array.Empty<ToolGatewayMiddleware>();
        _contextSetter = contextSetter;
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallOutcome> DispatchAsync(
        ToolCallRequest request,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Journal replay: bypasses gateway chain entirely (durable execution concern).
        // Cache-replay is only meaningful when the agent has stamped a RunId on the
        // ambient context AND a real journal is wired.
        if (context.RunId is { } runId && !ReferenceEquals(_journal, NullAgentJournal.Instance))
        {
            await foreach (var entry in _journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false))
            {
                if (entry is ToolCallRecorded recorded && recorded.CallId == request.CallId)
                {
                    // Cache hit — emit a single ToolCallReplayed event so observability
                    // backends can tell the difference between "tool ran" and "tool
                    // outcome served from the journal". No ToolCallStarted/Completed
                    // pair to avoid double-counting against the first dispatch.
                    await _eventBus.PublishAsync(
                        new ToolCallReplayed(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName),
                        cancellationToken).ConfigureAwait(false);
                    return recorded.Outcome;
                }
            }
        }

        // Eval baseline replay: match by (ToolName, Arguments) in the baseline run's journal.
        // On miss, fall through to live invocation (partial cache is allowed).
        if (context.BaselineRunId is { } baselineRunId && !ReferenceEquals(_journal, NullAgentJournal.Instance))
        {
            var argsText = request.Arguments.ToString();
            await foreach (var entry in _journal.ReadAsync(baselineRunId, cancellationToken).ConfigureAwait(false))
            {
                if (entry is ToolCallRecorded recorded
                    && recorded.ToolName == request.ToolName
                    && recorded.Arguments.ToString() == argsText)
                {
                    await _eventBus.PublishAsync(
                        new ToolCallReplayed(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName),
                        cancellationToken).ConfigureAwait(false);
                    return recorded.Outcome;
                }
            }
        }

        // Fast path: no gateway middleware registered — avoid context allocation.
        if (_gatewayMiddleware.Length == 0)
            return await InnerDispatchAsync(request, context, cancellationToken).ConfigureAwait(false);

        var gwCtx = new ToolGatewayContext(
            request.ToolName, request.CallId, request.Arguments, context);

        // Build right-to-left middleware pipeline; innermost = InnerDispatchAsync.
        // Each middleware is wrapped in a child vais.gateway.tool.middleware/<Name> span.
        Func<Task<ToolCallOutcome>> chain =
            () => InnerDispatchAsync(request, context, cancellationToken);
        for (var i = _gatewayMiddleware.Length - 1; i >= 0; i--)
        {
            var mw = _gatewayMiddleware[i];
            var middlewareName = mw.GetType().Name;
            var prev = chain;
            chain = async () =>
            {
                using var span = AgenticDiagnostics.ActivitySource
                    .StartActivity($"vais.gateway.tool.middleware/{middlewareName}");
                span?.SetTag("middleware.name", middlewareName);
                span?.SetTag("middleware.kind", "builtin");
                try
                {
                    return await mw.InvokeAsync(gwCtx, prev, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    span?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
            };
        }

        return await chain().ConfigureAwait(false);
    }

    // All of the original DispatchAsync body lives here: AllowedTools, registry, guardrails,
    // events, tool invocation, and journal write. Gateway chain is the outer layer.
    private async Task<ToolCallOutcome> InnerDispatchAsync(
        ToolCallRequest request,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        // AllowedTools enforcement: when the context carries an explicit tool allow-list,
        // reject any tool not on it before hitting the registry or guardrails.
        // null AllowedTools = no restriction (default for all pre-RCB deployments).
        if (context.AllowedTools is { } allowedTools && !allowedTools.Contains(request.ToolName))
        {
            var denied = new ToolCallOutcome(
                request.CallId,
                Result: $"Tool '{request.ToolName}' is not in the allowed-tools list for this context.",
                Error: nameof(UnauthorizedAccessException));
            return denied;
        }

        var tool = _toolRegistry?.GetByName(request.ToolName);
        if (tool is null)
        {
            var notFound = new ToolCallOutcome(
                request.CallId,
                Result: $"Tool '{request.ToolName}' not found in registry.",
                Error: nameof(KeyNotFoundException));
            await TryAppendJournalAsync(request, notFound, context, cancellationToken).ConfigureAwait(false);
            return notFound;
        }

        foreach (var guardrail in _toolGuardrails)
        {
            var outcome = await guardrail.BeforeInvokeAsync(tool, request.Arguments, context, cancellationToken).ConfigureAwait(false);
            await HandleToolGuardrailOutcomeAsync(outcome, context, cancellationToken).ConfigureAwait(false);
        }

        await _eventBus.PublishAsync(
            new ToolCallStarted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName),
            cancellationToken).ConfigureAwait(false);

        using var activity = AgenticDiagnostics.ActivitySource.StartActivity(
            $"tool.call/{request.ToolName}", ActivityKind.Internal);
        activity?.SetTag(AgenticTags.ToolName, request.ToolName);
        activity?.SetTag(AgenticTags.ToolCallId, request.CallId);
        var argsText = request.Arguments.ToString();
        if (argsText.Length > 0)
        {
            activity?.SetTag("gen_ai.prompt", argsText);
        }

        var sw = Stopwatch.StartNew();
        string result;
        string? toolError = null;
        try
        {
            // Make the run-stamped context ambient for the tool call so tools that read
            // IAgentContextAccessor.Current (e.g. run_code) see the current RunId + identity.
            // Tightly scoped to the invocation and restored on dispose; no-op without a setter.
            using (_contextSetter?.Push(context))
            {
                result = await tool.InvokeAsync(request.Arguments, cancellationToken).ConfigureAwait(false);
            }
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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(AgenticTags.ErrorType, toolError);
            // The tool genuinely failed (span stays Error), but the dispatcher feeds the error
            // back to the model rather than aborting the turn — a *recovered* mechanical failure.
            // Level=Warning lets the run-health rollup distinguish it from a turn-fatal error.
            await _eventBus.PublishAsync(
                new ToolCallCompleted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName, Succeeded: false, Error: toolError, sw.Elapsed)
                {
                    Level = FailureLevel.Warning,
                },
                cancellationToken).ConfigureAwait(false);
            var failed = new ToolCallOutcome(
                request.CallId,
                Result: $"Tool '{request.ToolName}' failed: {ex.Message}",
                Error: toolError);
            await TryAppendJournalAsync(request, failed, context, cancellationToken).ConfigureAwait(false);
            return failed;
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

        activity?.SetTag("gen_ai.completion", result);
        activity?.SetStatus(ActivityStatusCode.Ok);

        await _eventBus.PublishAsync(
            new ToolCallCompleted(DateTimeOffset.UtcNow, context, request.CallId, request.ToolName, Succeeded: true, Error: null, sw.Elapsed),
            cancellationToken).ConfigureAwait(false);

        var success = new ToolCallOutcome(request.CallId, result);
        await TryAppendJournalAsync(request, success, context, cancellationToken).ConfigureAwait(false);
        return success;
    }

    private ValueTask TryAppendJournalAsync(
        ToolCallRequest request,
        ToolCallOutcome outcome,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        if (context.RunId is not { } runId || ReferenceEquals(_journal, NullAgentJournal.Instance))
        {
            return ValueTask.CompletedTask;
        }

        var entry = new ToolCallRecorded(
            RunId: runId,
            CallId: request.CallId,
            ToolName: request.ToolName,
            Arguments: request.Arguments,
            Outcome: outcome,
            At: DateTimeOffset.UtcNow);
        // Exceptions propagate — journal is load-bearing for resume correctness.
        return _journal.AppendAsync(entry, cancellationToken);
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
                // Stamp RunId so callers can round-trip it into ResumeInput.
                var stamped = outcome.InterruptPayload with { RunId = context.RunId };
                await _eventBus.PublishAsync(
                    new InterruptRaised(DateTimeOffset.UtcNow, context, stamped.InterruptId, stamped.Reason),
                    cancellationToken).ConfigureAwait(false);
                throw new AgentInterruptedException(stamped);
        }
    }
}
