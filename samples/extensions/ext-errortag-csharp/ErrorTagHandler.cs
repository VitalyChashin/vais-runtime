// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

[assembly: Vais.Agents.VaisExtension(
    TargetApiVersion = "0.33",
    Handlers = new[] { typeof(Vais.Agents.Samples.Extensions.ErrorTag.TenantErrorTag) })]

namespace Vais.Agents.Samples.Extensions.ErrorTag;

/// <summary>
/// Sample <see cref="ErrorInterceptor"/> extension: prefixes the user-facing failure message with a
/// tenant tag and logs the failure for audit. Demonstrates <c>host: csharp</c> extension-authored
/// error handling — the same hook a co-tenant container agent gets via <c>host: container</c>
/// (see ext-errortag-python, mirrored shape).
/// </summary>
/// <remarks>
/// It only rewrites the human-facing message. It never changes <see cref="ErrorContext.ErrorType"/>
/// and never suppresses the failure — the exception still propagates and the failure event still
/// carries the original type and stack (P9).
/// </remarks>
public sealed class TenantErrorTag : ErrorInterceptor
{
    private readonly ILogger<TenantErrorTag> _log;

    public TenantErrorTag(ILogger<TenantErrorTag> log) => _log = log;

    public override Task<ErrorOutcome> OnErrorAsync(
        ErrorContext context, CancellationToken cancellationToken = default)
    {
        _log.LogError("[ext-errortag] failure agent={Agent} run={Run} node={Node} type={Type}: {Message}",
            context.AgentId, context.RunId, context.NodeId, context.ErrorType, context.ErrorMessage);

        return Task.FromResult(new ErrorOutcome(
            $"[acme-corp] {context.ErrorMessage} (ref: {context.RunId ?? "n/a"})"));
    }
}
