// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Outcome returned by an instrumented handler invocation lambda.
/// Carries the action label and, for skip/fail, the originating exception so the
/// span can be marked Error even when the exception is swallowed (failureMode=skip).
/// </summary>
internal readonly record struct HandlerOutcome(string ActionLabel, Exception? Exception = null)
{
    internal static HandlerOutcome Next() => new("next");
    internal static HandlerOutcome Mutate() => new("mutate");
    internal static HandlerOutcome ShortCircuit() => new("shortCircuit");
    internal static HandlerOutcome Skip(Exception ex) => new("skip", ex);
}

/// <summary>
/// Single emission site for <c>vais.extension.handler.invoke</c> spans and
/// <c>vais_extension_handler_invoke_*</c> metrics. Used by both
/// <c>HttpContainerHandlerProxy</c> (container host) and the instrumented
/// middleware decorators produced by <c>DefaultExtensionChainComposer</c>
/// (in-process host).
/// </summary>
internal static class ExtensionInvocationInstrumentation
{
    /// <summary>
    /// Wraps <paramref name="invoke"/> in a <c>vais.extension.handler.invoke</c>
    /// activity and records duration + count metrics on completion.
    /// <para>
    /// The lambda returns a <see cref="HandlerOutcome"/> with the action label
    /// (<c>next</c>, <c>mutate</c>, <c>shortCircuit</c>, <c>skip</c>). When the
    /// lambda returns <c>skip</c> with a non-null exception the span is tagged Error
    /// even though the exception was swallowed. If the lambda throws, the label is
    /// forced to <c>fail</c> and the exception is re-thrown after recording.
    /// </para>
    /// </summary>
    internal static async Task InvokeWithInstrumentationAsync(
        HandlerBindingDescriptor descriptor,
        string agentId,
        string? runId,
        string? nodeId,
        Func<Task<HandlerOutcome>> invoke,
        CancellationToken ct)
    {
        using var activity = ExtensionTelemetry.ActivitySource.StartActivity(
            "vais.extension.handler.invoke",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("vais.extension.id", descriptor.ExtensionId);
            activity.SetTag("vais.extension.version", descriptor.Version);
            activity.SetTag("vais.handler.id", descriptor.HandlerId);
            activity.SetTag("vais.seam", descriptor.Seam);
            activity.SetTag("vais.handler.host", descriptor.Host);
            activity.SetTag("vais.agent.id", agentId);
            if (runId is not null) activity.SetTag("vais.run.id", runId);
            if (nodeId is not null) activity.SetTag("vais.node.id", nodeId);
        }

        var startTs = Stopwatch.GetTimestamp();
        HandlerOutcome outcome;
        try
        {
            outcome = await invoke().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetTag("vais.handler.action", "fail");
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordMetrics(descriptor, "fail", Stopwatch.GetElapsedTime(startTs).TotalSeconds);
            throw;
        }

        activity?.SetTag("vais.handler.action", outcome.ActionLabel);
        if (outcome.Exception is { } ex2)
        {
            activity?.SetTag("error.type", ex2.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex2.Message);
        }

        RecordMetrics(descriptor, outcome.ActionLabel, Stopwatch.GetElapsedTime(startTs).TotalSeconds);
    }

    private static void RecordMetrics(HandlerBindingDescriptor d, string action, double seconds)
    {
        var tags = new TagList
        {
            { "extension", d.ExtensionId },
            { "version", d.Version },
            { "handler", d.HandlerId },
            { "seam", d.Seam },
            { "host", d.Host },
            { "action", action },
        };
        ExtensionTelemetry.InvokeDuration.Record(seconds, tags);
        ExtensionTelemetry.InvokeTotal.Add(1, tags);
    }
}
