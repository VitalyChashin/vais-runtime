// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

[assembly: Vais.Agents.VaisExtension(
    TargetApiVersion = "0.30",
    Handlers = new[] { typeof(Vais.Agents.Samples.Extensions.ToolDeny.TenantToolDeny) })]

namespace Vais.Agents.Samples.Extensions.ToolDeny;

/// <summary>
/// Sample <see cref="ToolGatewayMiddleware"/> extension: denies a fixed set of dangerous tools on
/// the tool gateway. Demonstrates <c>host: csharp</c> extension-authored tool governance — the same
/// deny a co-tenant container agent would get via <c>host: container</c> (see ext-tooldeny-python).
/// Short-circuits by returning a <see cref="ToolCallOutcome"/> with a non-null <c>Error</c> without
/// calling <c>next</c>; the model sees the error string and adapts.
/// </summary>
public sealed class TenantToolDeny : ToolGatewayMiddleware
{
    private static readonly FrozenSet<string> Denied =
        new[] { "shell", "delete_file" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<TenantToolDeny> _log;

    public TenantToolDeny(ILogger<TenantToolDeny> log) => _log = log;

    public override Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context, Func<Task<ToolCallOutcome>> next, CancellationToken cancellationToken = default)
    {
        if (Denied.Contains(context.ToolName))
        {
            _log.LogWarning("[ext-tooldeny] denied tool={Tool} agent={Agent}",
                context.ToolName, context.AgentContext.AgentName);
            return Task.FromResult(new ToolCallOutcome(
                context.CallId,
                Result: null,
                Error: $"ToolDenied: '{context.ToolName}' is blocked by the ext-tooldeny extension."));
        }

        return next();
    }
}
