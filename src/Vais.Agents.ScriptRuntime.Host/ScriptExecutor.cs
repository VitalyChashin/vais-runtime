// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;

namespace Vais.Agents.ScriptRuntime.Host;

/// <summary>
/// Runs a single code-mode script in a sandboxed Jint engine. No CLR access (the script can
/// reach only the injected <c>__callTool</c> and <c>console</c>), cooperative resource limits
/// (timeout / statements / memory / recursion), a tool-call budget, and an output-size cap.
/// The script's tool calls route to the runtime's container-gateway tool-invoke endpoint,
/// authenticated with the per-run call token carried on the request.
/// </summary>
public sealed class ScriptExecutor(HttpClient http, ILogger<ScriptExecutor> logger)
{
    internal static readonly ActivitySource ActivitySource = new("Vais.Agents.ScriptRuntime", "1.0.0");

    private const int MaxConsoleLines = 200;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Injects a console shim that forwards every channel to the host capture buffer.
    private const string ConsoleBootstrap =
        "var console={" +
        "log:function(){__log(Array.prototype.slice.call(arguments).join(' '));}," +
        "info:function(){__log(Array.prototype.slice.call(arguments).join(' '));}," +
        "warn:function(){__log(Array.prototype.slice.call(arguments).join(' '));}," +
        "error:function(){__log(Array.prototype.slice.call(arguments).join(' '));}," +
        "debug:function(){__log(Array.prototype.slice.call(arguments).join(' '));}};";

    /// <summary>Execute <paramref name="request"/> and return its result or a classified error. Never throws for script-level failures.</summary>
    public ScriptRunResponse Execute(ScriptRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = request.Limits;
        var console = new List<string>();
        var toolCalls = 0;
        var sw = Stopwatch.StartNew();

        using var activity = StartActivity(request);

        try
        {
            var engine = new Engine(options =>
            {
                options.LimitMemory(limits.MemoryBytes);
                options.TimeoutInterval(TimeSpan.FromMilliseconds(limits.TimeoutMs));
                options.MaxStatements(limits.MaxStatements);
                options.LimitRecursion(limits.RecursionDepth);
                options.CancellationToken(cancellationToken);
                // Deliberately NOT AllowClr() — the script gets no .NET types, filesystem, or network.
            });

            engine.SetValue("__log", new Action<string>(line =>
            {
                if (console.Count < MaxConsoleLines)
                    console.Add(Truncate(line, 2_000));
            }));

            engine.SetValue("__callTool", new Func<string, string, string, string>((server, tool, argsJson) =>
            {
                if (Interlocked.Increment(ref toolCalls) > limits.MaxToolCalls)
                    throw new ToolCallLimitException(limits.MaxToolCalls);
                return CallTool(request, server, tool, argsJson, toolCalls, cancellationToken);
            }));

            engine.Execute(ConsoleBootstrap);
            engine.Execute(request.Prelude);
            var completion = engine.Evaluate("(function(){\n" + request.Script + "\n})();");

            var result = Marshal(engine, completion, limits.MaxOutputBytes);
            sw.Stop();
            activity?.SetTag("vais.script.tool_calls", toolCalls);
            return new ScriptRunResponse
            {
                Result = result,
                Console = console,
                ToolCallCount = toolCalls,
                WallMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var error = Classify(ex);
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            logger.LogWarning(ex, "code-mode script failed run={RunId} agent={AgentId} type={ErrorType}",
                request.RunId, request.AgentId, error.Type);
            return new ScriptRunResponse
            {
                Console = console,
                ToolCallCount = toolCalls,
                Error = error,
                WallMs = sw.ElapsedMilliseconds,
            };
        }
    }

    private string CallTool(ScriptRunRequest request, string server, string tool, string argsJson, int callIndex, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            args = empty.RootElement.Clone();
        }

        var body = new GatewayToolInvokeRequest { ToolName = tool, Arguments = args, ToolCallId = $"sc-{callIndex}" };
        using var msg = new HttpRequestMessage(HttpMethod.Post, request.ToolGatewayUrl)
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        msg.Headers.TryAddWithoutValidation("X-Run-Id", request.RunId);
        msg.Headers.TryAddWithoutValidation("X-Agent-Id", request.AgentId);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.CallToken);

        // Propagate the script span as W3C traceparent so the gateway-side tool.call span nests
        // under it (this thread's Activity.Current is the scriptruntime.run span). Manual injection
        // avoids pulling in the HTTP-instrumentation package just for context propagation.
        if (Activity.Current?.Id is { } traceparent)
        {
            msg.Headers.TryAddWithoutValidation("traceparent", traceparent);
        }

        using var resp = http.Send(msg, ct);
        var payload = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new ToolGatewayException($"tool '{server}.{tool}' gateway returned {(int)resp.StatusCode}");

        var parsed = JsonSerializer.Deserialize<GatewayToolInvokeResponse>(payload, JsonOpts)
            ?? throw new ToolGatewayException($"tool '{server}.{tool}' returned an empty response");
        if (parsed.IsError)
            throw new ToolGatewayException($"tool '{server}.{tool}' errored: {parsed.Content}");
        return parsed.Content;
    }

    private static string? Marshal(Engine engine, JsValue completion, int maxOutputBytes)
    {
        if (completion.IsNull() || completion.IsUndefined())
            return null;
        if (completion.IsString())
            return Truncate(completion.AsString(), maxOutputBytes);

        // Non-string: serialize via the engine's own JSON.stringify (version-robust vs the Jint serializer API).
        engine.SetValue("__result", completion);
        var json = engine.Evaluate("JSON.stringify(__result)");
        var text = json.IsString() ? json.AsString() : json.ToString();
        return Truncate(text, maxOutputBytes);
    }

    private static ScriptRunError Classify(Exception ex) => ex switch
    {
        ToolCallLimitException => new ScriptRunError("ToolCallLimit", ex.Message),
        ToolGatewayException => new ScriptRunError("ToolError", ex.Message),
        Jint.Runtime.JavaScriptException jse => new ScriptRunError("ScriptError", jse.Message),
        OperationCanceledException => new ScriptRunError("Timeout", "script cancelled or timed out"),
        _ => new ScriptRunError(ClassifyByName(ex.GetType().Name), ex.Message),
    };

    // Jint's limit exceptions vary by version; classify defensively by type name to avoid
    // referencing types that may move between Jint releases.
    private static string ClassifyByName(string typeName) =>
        typeName.Contains("Timeout", StringComparison.Ordinal) ? "Timeout"
        : typeName.Contains("Cancel", StringComparison.Ordinal) ? "Timeout"
        : typeName.Contains("Memory", StringComparison.Ordinal) ? "MemoryLimit"
        : typeName.Contains("Statements", StringComparison.Ordinal) ? "StatementLimit"
        : typeName.Contains("Recursion", StringComparison.Ordinal) ? "RecursionLimit"
        : "ScriptError";

    private static string Truncate(string s, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var bytes = Encoding.UTF8.GetBytes(s);
        // Trim to a valid UTF-8 boundary at or below maxBytes.
        var len = Math.Min(maxBytes, bytes.Length);
        while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--; // don't cut a continuation byte
        return Encoding.UTF8.GetString(bytes, 0, len) + "…[truncated]";
    }

    private static Activity? StartActivity(ScriptRunRequest request)
    {
        if (!string.IsNullOrEmpty(request.Traceparent)
            && ActivityContext.TryParse(request.Traceparent, null, out var parent))
        {
            return ActivitySource.StartActivity("scriptruntime.run", ActivityKind.Internal, parent);
        }
        return ActivitySource.StartActivity("scriptruntime.run");
    }
}
