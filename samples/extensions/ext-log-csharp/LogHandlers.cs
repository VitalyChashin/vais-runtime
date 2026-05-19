// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

[assembly: Vais.Agents.VaisExtension(
    TargetApiVersion = "0.30",
    Handlers = new[] { typeof(Vais.Agents.Samples.Extensions.Log.LogInput), typeof(Vais.Agents.Samples.Extensions.Log.LogOutput) })]

namespace Vais.Agents.Samples.Extensions.Log;

/// <summary>Logs every agent input turn before the LLM call.</summary>
public sealed class LogInput : AgentInputMiddleware
{
    private readonly ILogger<LogInput> _log;

    public LogInput(ILogger<LogInput> log) => _log = log;

    public override async Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
    {
        _log.LogInformation("[ext-log] in  agent={Agent} msg={Message}", ctx.AgentId, ctx.Message);
        await next();
    }
}

/// <summary>Logs every LLM response (fires per round-trip in tool-calling loops).</summary>
public sealed class LogOutput : AgentOutputMiddleware
{
    private readonly ILogger<LogOutput> _log;

    public LogOutput(ILogger<LogOutput> log) => _log = log;

    public override async Task InvokeAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct = default)
    {
        _log.LogInformation("[ext-log] out agent={Agent} tokens={Tokens}", ctx.AgentId, ctx.Usage?.OutputTokens ?? 0);
        await next();
    }
}
