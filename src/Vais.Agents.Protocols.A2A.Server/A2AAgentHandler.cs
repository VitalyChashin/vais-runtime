// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Vais.Agents.Control;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// Bridges an A2A <see cref="IAgentHandler"/> onto Vais' <see cref="IAgentLifecycleManager"/>.
/// One instance per registered agent — bound to a specific <see cref="AgentHandle"/> at
/// construction so each A2A endpoint surfaces exactly one agent.
/// </summary>
/// <remarks>
/// <para><b>Response-shape rules</b> (from v0.8 plan §6 / §10):</para>
/// <list type="bullet">
///   <item><description>Fresh run (no existing <see cref="RequestContext.Task"/>) + successful single-turn reply → direct <see cref="Message"/> via <see cref="AgentEventQueue.EnqueueMessageAsync"/>. "Fast reply" path.</description></item>
///   <item><description>Fresh run + <see cref="AgentInterruptedException"/> → new <see cref="AgentTask"/> with <see cref="TaskState.InputRequired"/>, embedding <c>{interruptId, reason, runId, payload, agentId}</c> as a data-part on the status message.</description></item>
///   <item><description>Fresh run + <see cref="AgentPolicyDeniedException"/> or <see cref="AgentBudgetExceededException"/> → new <see cref="AgentTask"/> with <see cref="TaskState.Failed"/>, embedding <c>{code, operation?, field?, reason}</c> as a data-part on the status message.</description></item>
///   <item><description>Resume run (incoming <see cref="Message.TaskId"/> references an existing <see cref="TaskState.InputRequired"/> task) → reads prior interrupt metadata from task history, routes through <see cref="IAgentLifecycleManager.InvokeAsync"/> with <c>resume.*</c> metadata, transitions task to <see cref="TaskState.Completed"/> / <see cref="TaskState.InputRequired"/> / <see cref="TaskState.Failed"/>.</description></item>
/// </list>
/// <para>Cancellation (via <see cref="CancelAsync"/>) defers to the SDK's default — tasks transition to <see cref="TaskState.Canceled"/> with no Vais-side cleanup needed at the handler boundary.</para>
/// </remarks>
internal sealed class A2AAgentHandler : IAgentHandler
{
    /// <summary>Well-known metadata key on interrupt data-parts — identifies a Vais interrupt payload in task history during resume.</summary>
    internal const string InterruptMetadataKind = "vais.interrupt";

    private readonly IAgentLifecycleManager _lifecycle;
    private readonly AgentHandle _handle;

    public A2AAgentHandler(IAgentLifecycleManager lifecycle, AgentHandle handle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(handle);
        _lifecycle = lifecycle;
        _handle = handle;
    }

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(eventQueue);

        var isResume = context.Task is not null;
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        var text = ExtractText(context.Message);
        var metadata = BuildInvocationMetadata(context);

        if (isResume)
        {
            // Replay the existing task first so the SDK's MaterializeResponseAsync
            // captures a Task response — subsequent StatusUpdates we emit mutate the
            // store, and the SDK re-fetches the final task state at the end of the turn.
            // Without this, RequireInputAsync / CompleteAsync / FailAsync emit only
            // TaskStatusUpdateEvents, and the SDK errors with "did not produce any
            // response events" because StatusUpdateEvent doesn't count as a unary response.
            await eventQueue.EnqueueTaskAsync(context.Task!, cancellationToken).ConfigureAwait(false);
            await HandleResumeAsync(context, updater, text, metadata, cancellationToken).ConfigureAwait(false);
            return;
        }

        await HandleFreshAsync(context, eventQueue, updater, text, metadata, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task HandleFreshAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        TaskUpdater updater,
        string text,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _lifecycle.InvokeAsync(
                _handle,
                new AgentInvocationRequest(text, context.ContextId, metadata),
                cancellationToken).ConfigureAwait(false);

            await eventQueue.EnqueueMessageAsync(BuildReplyMessage(context.ContextId, result.Text), cancellationToken).ConfigureAwait(false);
        }
        catch (AgentInterruptedException ex)
        {
            // Full lifecycle transition so the task lands in the store with a history the
            // SDK can later resume: Submit → StartWork → RequireInput.
            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);
            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await EmitInputRequiredAsync(updater, context.ContextId, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentPolicyDeniedException ex)
        {
            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);
            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await EmitFailureAsync(updater, context.ContextId, BuildPolicyDeniedData(ex), cancellationToken).ConfigureAwait(false);
        }
        catch (AgentBudgetExceededException ex)
        {
            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);
            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await EmitFailureAsync(updater, context.ContextId, BuildBudgetExceededData(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleResumeAsync(
        RequestContext context,
        TaskUpdater updater,
        string text,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var prior = FindLastInterruptEnvelope(context.Task!);
        if (prior is not null)
        {
            if (prior.Value.TryGetProperty("interruptId", out var interruptId) && interruptId.ValueKind == JsonValueKind.String)
            {
                metadata["resume.interruptId"] = interruptId.GetString()!;
            }
            if (prior.Value.TryGetProperty("runId", out var runId) && runId.ValueKind == JsonValueKind.String)
            {
                metadata["resume.runId"] = runId.GetString()!;
            }
        }

        // Resume paths skip Submit/StartWork — the task already exists in the store (the
        // original interrupt-call submitted it), and ExecuteAsync already re-enqueued the
        // task event so MaterializeResponseAsync has a Task response to refetch.
        try
        {
            var result = await _lifecycle.InvokeAsync(
                _handle,
                new AgentInvocationRequest(text, context.ContextId, metadata),
                cancellationToken).ConfigureAwait(false);

            await updater.CompleteAsync(
                BuildReplyMessage(context.ContextId, result.Text),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentInterruptedException ex)
        {
            await EmitInputRequiredAsync(updater, context.ContextId, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentPolicyDeniedException ex)
        {
            await EmitFailureAsync(updater, context.ContextId, BuildPolicyDeniedData(ex), cancellationToken).ConfigureAwait(false);
        }
        catch (AgentBudgetExceededException ex)
        {
            await EmitFailureAsync(updater, context.ContextId, BuildBudgetExceededData(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EmitInputRequiredAsync(
        TaskUpdater updater,
        string contextId,
        AgentInterruptedException ex,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["interruptId"] = ex.Interrupt.InterruptId,
            ["reason"] = ex.Interrupt.Reason,
            ["runId"] = ex.Interrupt.RunId,
            ["agentId"] = _handle.AgentId,
        };
        if (ex.Interrupt.Payload.ValueKind != JsonValueKind.Undefined &&
            ex.Interrupt.Payload.ValueKind != JsonValueKind.Null)
        {
            payload["payload"] = JsonNode.Parse(ex.Interrupt.Payload.GetRawText());
        }
        var dataPart = Part.FromData(JsonSerializer.SerializeToElement(payload));
        dataPart.Metadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        dataPart.Metadata[InterruptMetadataKind] = JsonSerializer.SerializeToElement(true);

        var message = new Message
        {
            Role = Role.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = contextId,
        };
        message.Parts.Add(dataPart);

        await updater.RequireInputAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EmitFailureAsync(
        TaskUpdater updater,
        string contextId,
        JsonObject errorData,
        CancellationToken cancellationToken)
    {
        var dataPart = Part.FromData(JsonSerializer.SerializeToElement(errorData));
        var message = new Message
        {
            Role = Role.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = contextId,
        };
        message.Parts.Add(dataPart);

        await updater.FailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject BuildPolicyDeniedData(AgentPolicyDeniedException ex) => new()
    {
        ["code"] = "policy-denied",
        ["operation"] = ex.Operation.ToString(),
        ["reason"] = ex.Reason,
    };

    private static JsonObject BuildBudgetExceededData(AgentBudgetExceededException ex) => new()
    {
        ["code"] = "budget-exceeded",
        ["field"] = ex.BudgetField,
    };

    private static Message BuildReplyMessage(string contextId, string text)
    {
        var reply = new Message
        {
            Role = Role.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = contextId,
        };
        reply.Parts.Add(Part.FromText(text));
        return reply;
    }

    private static Dictionary<string, string> BuildInvocationMetadata(RequestContext context) => new(StringComparer.Ordinal)
    {
        ["a2a.taskId"] = context.TaskId,
        ["a2a.contextId"] = context.ContextId,
    };

    /// <summary>
    /// Locate the interrupt envelope from a prior <c>RequireInputAsync</c> call on this task.
    /// Checks the task's status message first (where <c>TaskUpdater.RequireInputAsync</c>
    /// stows it) then walks <see cref="AgentTask.History"/> newest→oldest for a data-part
    /// tagged with <see cref="InterruptMetadataKind"/>. The part's JSON value is the envelope we
    /// emitted on interrupt, so its <c>interruptId</c> / <c>runId</c> fields identify the
    /// interrupt the client is resuming.
    /// </summary>
    internal static JsonElement? FindLastInterruptEnvelope(AgentTask task)
    {
        // Most recent interrupt is on the task's current status message.
        if (task.Status?.Message is { } statusMsg &&
            TryExtractInterruptEnvelope(statusMsg, out var envelope))
        {
            return envelope;
        }

        if (task.History is null)
        {
            return null;
        }
        for (var i = task.History.Count - 1; i >= 0; i--)
        {
            if (TryExtractInterruptEnvelope(task.History[i], out var historyEnvelope))
            {
                return historyEnvelope;
            }
        }
        return null;
    }

    private static bool TryExtractInterruptEnvelope(Message msg, out JsonElement envelope)
    {
        envelope = default;
        if (msg.Parts is null)
        {
            return false;
        }
        foreach (var part in msg.Parts)
        {
            if (part.ContentCase != PartContentCase.Data)
            {
                continue;
            }
            if (part.Metadata is null || !part.Metadata.ContainsKey(InterruptMetadataKind))
            {
                continue;
            }
            if (part.Data is { } data)
            {
                envelope = data;
                return true;
            }
        }
        return false;
    }

    internal static string ExtractText(Message? message)
    {
        if (message?.Parts is null || message.Parts.Count == 0)
        {
            return string.Empty;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var part in message.Parts)
        {
            if (part.ContentCase != PartContentCase.Text)
            {
                continue;
            }
            var text = part.Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(text);
        }
        return sb.ToString();
    }
}
