// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vais.Agents.Control;

namespace Vais.Agents.Protocols.Mcp.Server;

/// <summary>
/// Builds an <see cref="McpServerOptions"/> that wraps an <see cref="IAgentRegistry"/> +
/// <see cref="IAgentLifecycleManager"/> into an MCP server per the v0.7 pillar plan:
/// one MCP tool per registered agent, <c>{text, sessionId?, resume?}</c> input schema,
/// interrupt → <c>isError: true</c> with a continuation payload, resume-arg → <c>ResumeAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not use the SDK's <c>[McpServerTool]</c> attribute pattern?</b> We discover
/// agents dynamically from the registry at list-tools time — the attribute pattern
/// is static. We hook <see cref="McpServerHandlers.ListToolsHandler"/> +
/// <see cref="McpServerHandlers.CallToolHandler"/> directly so <c>list_tools</c>
/// reflects the current registry state on every call.
/// </para>
/// </remarks>
public static class McpAgentServerBuilder
{
    /// <summary>
    /// Input-schema JSON for every agent-tool: <c>{ text: required string, sessionId?: string, resume?: { interruptId, payload } }</c>.
    /// </summary>
    internal const string AgentToolInputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "text":       { "type": "string", "description": "User message for the agent to process." },
            "sessionId":  { "type": "string", "description": "Optional conversation id. When supplied, the agent's history is preserved across calls under (agentId, sessionId). When absent, the call runs in a fresh session." },
            "resume": {
              "type": "object",
              "description": "Optional continuation of a prior interrupt. When present, `text` is ignored; the call routes to ResumeAsync with the supplied payload.",
              "properties": {
                "interruptId": { "type": "string" },
                "runId":       { "type": "string" },
                "payload":     {}
              },
              "required": ["interruptId"]
            }
          },
          "required": ["text"]
        }
        """;

    /// <summary>Create configured <see cref="McpServerOptions"/> wrapping the supplied agent plumbing.</summary>
    public static McpServerOptions Build(
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        McpAgentServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        options ??= new McpAgentServerOptions();

        var serverOptions = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = options.Name, Version = options.Version },
            ServerInstructions = options.Instructions,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (ctx, ct) => HandleListToolsAsync(registry, options, ct),
                CallToolHandler = (ctx, ct) => HandleCallToolAsync(registry, lifecycle, ctx.Params, ct),
            },
        };
        return serverOptions;
    }

    internal static async ValueTask<ListToolsResult> HandleListToolsAsync(
        IAgentRegistry registry,
        McpAgentServerOptions options,
        CancellationToken ct)
    {
        var inputSchema = JsonDocument.Parse(AgentToolInputSchemaJson).RootElement;
        var tools = new List<Tool>();
        await foreach (var manifest in registry.ListAsync(options.LabelPrefixFilter, ct).ConfigureAwait(false))
        {
            tools.Add(new Tool
            {
                Name = manifest.Id,
                Title = manifest.Description ?? manifest.Id,
                Description = BuildToolDescription(manifest),
                InputSchema = inputSchema,
            });
        }
        return new ListToolsResult { Tools = tools };
    }

    internal static async ValueTask<CallToolResult> HandleCallToolAsync(
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        CallToolRequestParams? request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrEmpty(request.Name))
        {
            return TextError("Missing tool name.");
        }

        var manifest = await registry.GetAsync(request.Name, version: null, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            return TextError($"Unknown agent '{request.Name}'. Call list_tools to see registered agents.");
        }
        var handle = new AgentHandle(manifest.Id, manifest.Version);

        var (text, sessionId, resumePayload, resumeInterruptId, resumeRunId) = ParseArgs(request.Arguments);

        try
        {
            if (resumeInterruptId is not null)
            {
                // Continuation of a previously-interrupted call. There's no lifecycle-manager
                // verb for resume (v0.6 didn't ship one), so we route through a fresh Invoke
                // with the payload text as the user message and tag the context via metadata.
                var resumeMeta = new Dictionary<string, string>
                {
                    ["resume.interruptId"] = resumeInterruptId,
                };
                if (resumeRunId is not null) resumeMeta["resume.runId"] = resumeRunId;
                var resumeText = resumePayload?.GetRawText() ?? text ?? string.Empty;
                var resumeResult = await lifecycle.InvokeAsync(
                    handle,
                    new AgentInvocationRequest(resumeText, sessionId, resumeMeta),
                    ct).ConfigureAwait(false);
                return TextSuccess(resumeResult.Text);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return TextError("Missing required 'text' argument.");
            }
            var result = await lifecycle.InvokeAsync(
                handle,
                new AgentInvocationRequest(text, sessionId),
                ct).ConfigureAwait(false);
            return TextSuccess(result.Text);
        }
        catch (AgentInterruptedException ex)
        {
            var payload = new JsonObject
            {
                ["interruptId"] = ex.Interrupt.InterruptId,
                ["reason"] = ex.Interrupt.Reason,
                ["runId"] = ex.Interrupt.RunId,
                ["agentId"] = manifest.Id,
                ["continuation"] = "Re-invoke this tool with arguments.resume = { interruptId, runId?, payload } to continue.",
            };
            return TextError(payload.ToJsonString());
        }
        catch (AgentPolicyDeniedException ex)
        {
            var payload = new JsonObject
            {
                ["code"] = "policy-denied",
                ["operation"] = ex.Operation.ToString(),
                ["reason"] = ex.Reason,
            };
            return TextError(payload.ToJsonString());
        }
        catch (AgentBudgetExceededException ex)
        {
            var payload = new JsonObject
            {
                ["code"] = "budget-exceeded",
                ["field"] = ex.BudgetField,
            };
            return TextError(payload.ToJsonString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TextError($"Agent invocation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (string? Text, string? SessionId, JsonElement? ResumePayload, string? ResumeInterruptId, string? ResumeRunId) ParseArgs(
        IDictionary<string, JsonElement>? args)
    {
        if (args is null)
        {
            return (null, null, null, null, null);
        }
        string? text = args.TryGetValue("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string? sessionId = args.TryGetValue("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        JsonElement? payload = null;
        string? interruptId = null;
        string? runId = null;
        if (args.TryGetValue("resume", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("interruptId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                interruptId = idEl.GetString();
            }
            if (r.TryGetProperty("runId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
            {
                runId = ridEl.GetString();
            }
            if (r.TryGetProperty("payload", out var pEl))
            {
                payload = pEl.Clone();
            }
        }
        return (text, sessionId, payload, interruptId, runId);
    }

    internal static string BuildToolDescription(AgentManifest manifest)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(manifest.Id).Append(" (v").Append(manifest.Version).Append(')');
        if (!string.IsNullOrEmpty(manifest.Description))
        {
            sb.Append(" — ").Append(manifest.Description);
        }
        if (manifest.Budget is { } b && (b.MaxTurns is not null || b.MaxDuration is not null))
        {
            sb.Append("\nBudget: ");
            var parts = new List<string>(4);
            if (b.MaxTurns is int mt) parts.Add($"maxTurns={mt}");
            if (b.MaxToolCalls is int mtc) parts.Add($"maxToolCalls={mtc}");
            if (b.MaxDuration is TimeSpan md) parts.Add($"maxDuration={md}");
            sb.Append(string.Join(", ", parts));
        }
        if (manifest.Handoffs is { Count: > 0 } handoffs)
        {
            sb.Append("\nHandoffs: ").Append(string.Join(", ", handoffs.Select(h => "→ " + h.ToAgent)));
        }
        sb.Append("\nInput: { text: '…', sessionId?: '…', resume?: { interruptId, runId?, payload } }");
        return sb.ToString();
    }

    private static CallToolResult TextSuccess(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = false,
    };

    private static CallToolResult TextError(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = true,
    };
}
