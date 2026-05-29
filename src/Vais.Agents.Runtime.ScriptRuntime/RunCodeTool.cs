// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// The single LLM-facing tool used by a code-mode agent. The model calls <c>run_code({code})</c>;
/// this tool ships the script (with the generated <c>tools.*</c> prelude) to the ScriptRuntime
/// sidecar, authenticated with a short-turn call token minted for the current run. On failure it
/// throws so the dispatcher records a real <c>ToolCallOutcome.Error</c> — never a fabricated success.
/// </summary>
internal sealed class RunCodeTool : ITool
{
    private static readonly JsonElement ParamsSchema = BuildSchema();

    private readonly string _prelude;
    private readonly CodeModeLimits _limits;
    private readonly IScriptRuntimeClient _client;
    private readonly ICallTokenService _callTokens;
    private readonly ScriptRuntimeOptions _options;
    private readonly ILogger<RunCodeTool> _logger;

    public RunCodeTool(
        string agentId,
        string prelude,
        CodeModeLimits limits,
        IScriptRuntimeClient client,
        ICallTokenService callTokens,
        ScriptRuntimeOptions options,
        ILogger<RunCodeTool> logger)
    {
        AgentId = agentId;
        _prelude = prelude;
        _limits = limits;
        _client = client;
        _callTokens = callTokens;
        _options = options;
        _logger = logger;
    }

    public string AgentId { get; }

    public string Name => "run_code";

    public string Description =>
        "Execute JavaScript that calls this agent's tools as functions and composes their results in a "
        + "single step (loops, filtering, conditionals). Call tools via tools[\"<name>\"](args) and `return` "
        + "the final value. Available tools:\n" + _prelude;

    public JsonElement ParametersSchema => ParamsSchema;

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var script = arguments.ValueKind == JsonValueKind.Object
                     && arguments.TryGetProperty("code", out var codeEl)
                     && codeEl.ValueKind == JsonValueKind.String
            ? codeEl.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new CodeModeExecutionException("Validation", "run_code requires a non-empty 'code' string argument.");
        }

        // Ambient run context — same accessor LocalAgentTool reads. Never null (returns Empty).
        var context = new AsyncLocalAgentContextAccessor().Current;
        var runId = context.RunId ?? context.CorrelationId ?? AgentId;
        var ttlSeconds = Math.Max(60, (_limits.TimeoutMs / 1000) + 30);
        var callToken = _callTokens.Generate(runId, AgentId, AgentContextClaims.From(context), ttlSeconds);

        var request = new ScriptRunRequest
        {
            RunId = runId,
            AgentId = AgentId,
            Prelude = _prelude,
            Script = script,
            ToolGatewayUrl = _options.GatewayBaseUrl.TrimEnd('/') + "/v1/container-gateway/tools/invoke",
            CallToken = callToken,
            Limits = _limits,
            Traceparent = System.Diagnostics.Activity.Current?.Id,
        };

        var response = await _client.RunAsync(request, cancellationToken).ConfigureAwait(false);

        // Surface script telemetry into the runtime side: the run_code tool-call span is
        // Activity.Current here, so tag it with script metrics, and forward the sidecar-captured
        // console output to the logs. (The sidecar also exports its own spans via OTLP when
        // configured; this makes script internals visible from the runtime even when it isn't.)
        var activity = System.Diagnostics.Activity.Current;
        activity?.SetTag("vais.code_mode.tool_calls", response.ToolCallCount);
        activity?.SetTag("vais.code_mode.wall_ms", response.WallMs);
        foreach (var line in response.Console)
        {
            _logger.LogInformation("code-mode console agent={AgentId} run={RunId}: {Line}", AgentId, runId, line);
        }

        if (response.Error is { } error)
        {
            activity?.SetTag("vais.code_mode.error_type", error.Type);
            _logger.LogWarning(
                "code-mode run_code failed agent={AgentId} run={RunId} type={ErrorType}",
                AgentId, runId, error.Type);
            throw new CodeModeExecutionException(error.Type, error.Message);
        }

        return response.Result ?? string.Empty;
    }

    private static JsonElement BuildSchema()
    {
        using var doc = JsonDocument.Parse(
            """
            {"type":"object","properties":{"code":{"type":"string","description":"JavaScript to execute. Call tools[\"name\"](args) and return the final value."}},"required":["code"],"additionalProperties":false}
            """);
        return doc.RootElement.Clone();
    }
}

/// <summary>A code-mode script execution failed; carries the sidecar's classified error type.</summary>
internal sealed class CodeModeExecutionException(string errorType, string message)
    : Exception($"code-mode {errorType}: {message}")
{
    public string ErrorType { get; } = errorType;
}
