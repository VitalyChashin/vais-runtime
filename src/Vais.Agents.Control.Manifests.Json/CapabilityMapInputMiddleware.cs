// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Plan C2-2 — an <see cref="AgentInputMiddleware"/> that resolves the coordinator's
/// <see cref="CapabilityMap"/> through <see cref="IAgentCapabilityMapBuilder"/> and surfaces
/// it to the agent two ways: (a) structured under
/// <see cref="AgentInputContext.Properties"/>[<see cref="ContextPropertyKey"/>], (b)
/// optionally prepended onto <see cref="AgentInputContext.Message"/> as a compact text block
/// so any existing agent picks it up in-band without code changes.
/// </summary>
/// <remarks>
/// Pure pass-through when the coordinator has no sub-agents — the map renders empty, the
/// Properties entry stays the empty map, and Message is untouched. Opt-in per coordinator
/// via the existing Extension scope matcher; behaviour identical to the
/// <c>samples/extensions/ext-log-csharp/LogInput</c> pattern.
/// </remarks>
public sealed class CapabilityMapInputMiddleware(
    IAgentCapabilityMapBuilder builder,
    CapabilityMapInputMiddlewareOptions? options = null) : AgentInputMiddleware
{
    /// <summary>Canonical <see cref="AgentInputContext.Properties"/> key for the structured map.</summary>
    public const string ContextPropertyKey = "vais.capability_map";

    private readonly IAgentCapabilityMapBuilder _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    private readonly CapabilityMapInputMiddlewareOptions _options = options ?? new();

    /// <inheritdoc />
    public override async Task InvokeAsync(
        AgentInputContext context,
        Func<Task> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var map = await _builder.BuildAsync(context.AgentId, cancellationToken).ConfigureAwait(false);
        context.Properties[ContextPropertyKey] = map;

        if (_options.InjectIntoMessage && map.SubAgents.Count > 0)
        {
            var text = map.ToCompactText();
            context.Message = string.IsNullOrEmpty(context.Message)
                ? text
                : text + "\n" + context.Message;
        }

        await next().ConfigureAwait(false);
    }
}

/// <summary>Configuration for <see cref="CapabilityMapInputMiddleware"/>.</summary>
public sealed record CapabilityMapInputMiddlewareOptions
{
    /// <summary>
    /// When <see langword="true"/> (default), prepend the compact text rendering of the
    /// capability map onto <see cref="AgentInputContext.Message"/> so any existing agent
    /// picks it up in-band. Disable when the consumer reads the map structurally from
    /// <see cref="AgentInputContext.Properties"/> only — keeps the user message clean.
    /// </summary>
    public bool InjectIntoMessage { get; init; } = true;
}
