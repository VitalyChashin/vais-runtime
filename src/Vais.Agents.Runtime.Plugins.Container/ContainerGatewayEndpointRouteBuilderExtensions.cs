// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins.Container.Otlp;
using Vais.Agents.Runtime.Plugins.Container.StructuredLog;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Maps the internal container gateway endpoints:
/// <c>POST /v1/container-gateway/llm/complete</c> and
/// <c>POST /v1/container-gateway/tools/invoke</c>.
/// Call token validation is enforced on all routes.
/// </summary>
public static class ContainerGatewayEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Adds the container gateway callback endpoints. Call this from the runtime host's
    /// pipeline setup on the internal port (typically 5001).
    /// </summary>
    public static IEndpointRouteBuilder MapContainerGatewayEndpoints(
        this IEndpointRouteBuilder builder)
    {
        var callTokenService = builder.ServiceProvider.GetRequiredService<ICallTokenService>();

        var group = builder.MapGroup("/v1/container-gateway");

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var runId = ctx.HttpContext.Request.Headers["X-Run-Id"].FirstOrDefault() ?? "";
            var agentId = ctx.HttpContext.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
            var authHeader = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault();
            var bearerToken = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? authHeader["Bearer ".Length..] : "";

            if (string.IsNullOrEmpty(bearerToken) || !callTokenService.Validate(bearerToken, runId, agentId))
                return Results.Unauthorized();

            return await next(ctx);
        });

        group.MapPost("llm/complete", HandleLlmCompleteAsync);
        group.MapPost("chat/completions", HandleChatCompletionsAsync);
        group.MapPost("tools/invoke", HandleToolInvokeAsync);
        group.MapGet("tools/list", HandleToolsListAsync);
        group.MapPost("sections/build", HandleSectionsBuildAsync);
        group.MapPost("token/renew", HandleTokenRenew);

        // Telemetry endpoints: OTLP spans + structured logs.
        // Both self-validate the vais-plugin-token — no outer filter needed.
        builder.MapPluginOtlpEndpoints();
        builder.MapPluginStructuredLogEndpoints();

        return builder;
    }

    /// <summary>
    /// Issues a fresh short-lived call token to a session-mode plugin. The outer filter has already
    /// validated the presented (current) token — so the plugin must renew before it expires — and
    /// confirmed it matches the X-Run-Id / X-Agent-Id headers. The renewed token carries the same
    /// identity (and, in lease mode, the same leaseId) with a new expiry.
    /// </summary>
    private static IResult HandleTokenRenew(HttpContext ctx, ICallTokenService callTokenService)
    {
        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        var ttl = ctx.RequestServices.GetService<ContainerPluginLoaderOptions>()?.RenewTokenTtlSeconds ?? 120;

        var token = callTokenService.Generate(runId, agentId, ttl);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl).ToUnixTimeSeconds();
        return Results.Ok(new TokenRenewResponse { Token = token, ExpiresAt = expiresAt });
    }

    private static async Task<IResult> HandleLlmCompleteAsync(
        HttpContext ctx,
        GatewayLlmCompleteRequest body,
        ICompletionProviderPool pool,
        IEnumerable<LlmGatewayMiddleware> gatewayMiddleware,
        CancellationToken ct)
    {
        // Discriminator: exactly one of Messages or Sections must be present (contract v0.27).
        var hasMessages = body.Messages is { Count: > 0 };
        var hasSections = body.Sections is { Count: > 0 };
        if (hasMessages == hasSections)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Exactly one of 'messages' or 'sections' must be present.",
                detail: hasMessages
                    ? "Both 'messages' and 'sections' were populated; pick one."
                    : "Neither 'messages' nor 'sections' is populated; supply one.",
                extensions: new Dictionary<string, object?>
                {
                    ["urn"] = "urn:vais-agents:llm-complete-input-conflict",
                });
        }

        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        var agentCtx = new AgentContext(AgentName: agentId) { RunId = runId };
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()?.Push(agentCtx);

        var modelId = string.IsNullOrEmpty(body.ModelId) ? "gpt-4o-mini" : body.ModelId;
        var provider = await pool.GetAsync(
            new ModelSpec("openai", modelId, ApiKeyRef: "secret://env/OPENAI_API_KEY"), ct)
            .ConfigureAwait(false);

        var streaming = ctx.Request.Headers.Accept.Any(
            h => h?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);

        // Build the CompletionRequest. For the messages variant the body is treated as the final
        // history (pre-flattened path; preserves the v0.26-and-earlier behaviour). For the sections
        // variant we run the full pipeline server-side — resolver → packer → telemetry emitter →
        // flattener — so per-section observability fires the same way it does for a runtime-hosted
        // agent (the regression that motivated v0.27).
        CompletionRequest request;
        if (hasMessages)
        {
            request = new CompletionRequest(
                body.Messages!.Select(PluginMessageToChatTurn).ToArray(),
                Temperature: body.Options?.Temperature,
                MaxTokens: body.Options?.MaxTokens);
        }
        else
        {
            var translator = ctx.RequestServices.GetService<IAgentManifestTranslator>();
            if (translator is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status501NotImplemented,
                    title: "Sections variant requires IAgentManifestTranslator registration.",
                    detail: "Register IAgentManifestTranslator in DI (the runtime host does so automatically). " +
                            "Hosts that don't register a translator support only the 'messages' variant.");
            }
            var pipelineResult = await BuildFromSectionsAsync(
                body.Sections!, body.Options, translator, agentId, agentCtx, ct).ConfigureAwait(false);
            if (pipelineResult.Error is not null)
            {
                return pipelineResult.Error;
            }
            request = pipelineResult.Request!;
        }

        if (streaming)
        {
            if (provider is not IStreamingCompletionProvider streamingProvider)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Provider does not support streaming.");
            }
            var stream = LlmGatewayPipeline.StreamAsync(request, streamingProvider, gatewayMiddleware, ct);
            await ContainerGatewayVaisSseWriter.WriteAsync(ctx.Response, stream, ct).ConfigureAwait(false);
            return Results.Empty;
        }

        var response = await LlmGatewayPipeline.InvokeAsync(request, provider, gatewayMiddleware, ct)
            .ConfigureAwait(false);

        return Results.Ok(new GatewayLlmCompleteResponse
        {
            Message = new PluginMessage { Role = "assistant", Content = response.Text },
            Usage = new PluginUsageCounts
            {
                InputTokens = response.PromptTokens ?? 0,
                OutputTokens = response.CompletionTokens ?? 0,
            },
        });
    }

    private readonly record struct SectionsBuildPipelineResult(CompletionRequest? Request, IResult? Error);

    private static async Task<SectionsBuildPipelineResult> BuildFromSectionsAsync(
        IReadOnlyList<GatewaySection> wireSections,
        GatewayLlmCompleteOptions? options,
        IAgentManifestTranslator translator,
        string agentId,
        AgentContext agentCtx,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return new SectionsBuildPipelineResult(null, Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing X-Agent-Id header — required for the sections variant."));
        }

        StatefulAgentOptions agentOptions;
        try
        {
            agentOptions = await translator.TranslateAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (ManifestInstantiationException ex)
        {
            return new SectionsBuildPipelineResult(null, Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: $"Agent '{agentId}' not found or not translatable.",
                detail: ex.Message));
        }

        // Wire → internal Section[]. Producer-id and budget round-trip.
        var sections = new List<Section>(wireSections.Count);
        foreach (var w in wireSections)
        {
            try
            {
                sections.Add(WireToSection(w));
            }
            catch (ArgumentException ex)
            {
                return new SectionsBuildPipelineResult(null, Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"Malformed section '{w.Id}'.",
                    detail: ex.Message));
            }
        }

        // Resolve → pack → telemetry → flatten. Same pipeline StatefulAiAgent runs internally.
        var resolver = agentOptions.SectionResolver ?? DefaultSectionResolver.Instance;
        IReadOnlyList<Section> resolved;
        try
        {
            resolved = await resolver.ResolveAsync(sections, ct).ConfigureAwait(false);
        }
        catch (SectionCollisionException ex)
        {
            return new SectionsBuildPipelineResult(null, Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Section id collision.",
                detail: ex.Message));
        }

        var packer = agentOptions.SectionWindowPacker
            ?? (agentOptions.ContextWindowPacker is not null
                ? new LegacyPackerAdapter(agentOptions.ContextWindowPacker)
                : DefaultSectionWindowPacker.Instance);
        var budget = agentOptions.SectionBudget ?? SectionBudgetContext.Unlimited;
        var packed = await packer.PackAsync(resolved, budget, ct).ConfigureAwait(false);

        var emitter = agentOptions.SectionTelemetrySinks.Count == 0
            ? SectionTelemetryEmitter.NoOp
            : new SectionTelemetryEmitter(agentOptions.SectionTelemetrySinks);
        if (!emitter.IsNoOp)
        {
            await emitter.EmitAsync(
                resolved, packed, budget, agentCtx, turnIndex: 1, ct).ConfigureAwait(false);
        }

        var template = new CompletionRequest(
            History: Array.Empty<ChatTurn>(),
            Temperature: options?.Temperature,
            MaxTokens: options?.MaxTokens);
        var flattened = CompletionRequestFlattener.Flatten(packed.Sections, template);
        return new SectionsBuildPipelineResult(flattened, null);
    }

    private static Section WireToSection(GatewaySection w)
    {
        if (!Enum.TryParse<SectionKind>(w.Kind, ignoreCase: false, out var kind))
        {
            throw new ArgumentException($"Unknown section kind '{w.Kind}'.");
        }

        SectionPayload payload = kind switch
        {
            SectionKind.SystemSegment => new TextPayload(w.Payload.Value ?? ""),
            SectionKind.UserMessage or SectionKind.AssistantMessage or SectionKind.ToolMessage =>
                new TurnPayload(PluginMessageToChatTurn(w.Payload.Turn
                    ?? throw new ArgumentException("turn payload required for turn-kind sections"))),
            SectionKind.ToolDeclaration => throw new ArgumentException(
                "ToolDeclaration sections cannot round-trip from the wire — tools are runtime registry-bound."),
            SectionKind.ResponseFormat => new ResponseFormatPayload(new ResponseFormatSpec(
                Schema: w.Payload.Spec?.Schema ?? throw new ArgumentException("response_format spec required"),
                SchemaName: w.Payload.Spec.Name,
                Strict: w.Payload.Spec.Strict)),
            SectionKind.Metadata => new MetadataPayload(
                (w.Payload.Values ?? new Dictionary<string, JsonElement>())
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value)),
            _ => throw new ArgumentException($"Unsupported section kind '{kind}'."),
        };

        return new Section(
            w.Id, kind, payload,
            Order: w.Order,
            ProducerId: w.ProducerId,
            Budget: w.Budget is null ? null : new SectionBudget(w.Budget.Priority, w.Budget.MaxChars));
    }

    private static async Task<IResult> HandleChatCompletionsAsync(
        HttpContext ctx,
        OpenAiChatRequest body,
        ICompletionProviderPool pool,
        IEnumerable<LlmGatewayMiddleware> gatewayMiddleware,
        CancellationToken ct)
    {
        var provider = await pool.GetAsync(
            new ModelSpec("openai", body.Model, ApiKeyRef: "secret://env/OPENAI_API_KEY"), ct)
            .ConfigureAwait(false);

        var history = body.Messages
            .Select(OpenAiMessageToChatTurn)
            .ToArray();

        ResponseFormatSpec? responseFormat = null;
        if (body.ResponseFormat is { Type: "json_schema", JsonSchema: { } js })
            responseFormat = new ResponseFormatSpec(js.Schema, js.Name, js.Strict ?? true);

        var request = new CompletionRequest(
            history,
            Temperature:    body.Temperature,
            MaxTokens:      body.MaxTokens,
            ResponseFormat: responseFormat);

        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()
            ?.Push(new AgentContext(AgentName: agentId) { RunId = runId });

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        if (body.Stream == true)
        {
            if (provider is not IStreamingCompletionProvider streamingProvider)
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);

            var stream = LlmGatewayPipeline.StreamAsync(request, streamingProvider, gatewayMiddleware, ct);
            await ContainerGatewaySseWriter.WriteAsync(ctx.Response, completionId, body.Model, stream, ct)
                .ConfigureAwait(false);
            return Results.Empty;
        }

        var response = await LlmGatewayPipeline.InvokeAsync(request, provider, gatewayMiddleware, ct)
            .ConfigureAwait(false);

        return Results.Ok(new OpenAiChatResponse
        {
            Id      = completionId,
            Object  = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model   = body.Model,
            Choices = [new OpenAiChatChoice
            {
                Index        = 0,
                Message      = new OpenAiChatMessage { Role = "assistant", Content = response.Text },
                FinishReason = "stop",
            }],
            Usage = new OpenAiUsage
            {
                PromptTokens     = response.PromptTokens     ?? 0,
                CompletionTokens = response.CompletionTokens ?? 0,
                TotalTokens      = (response.PromptTokens ?? 0) + (response.CompletionTokens ?? 0),
            },
        });
    }

    private static async Task<IResult> HandleToolInvokeAsync(
        HttpContext ctx,
        GatewayToolInvokeRequest body,
        IMcpServerRegistry? registry,
        IEnumerable<INamedToolSourceProvider> providers,
        IEnumerable<IToolGuardrail> guardrails,
        IEnumerable<ToolGatewayMiddleware> toolMiddleware,
        CancellationToken ct)
    {
        if (registry is null)
            return ToolNotFound(body.ToolCallId, body.ToolName, "no tool registry");

        var tool = await FindToolAsync(body.ToolName, registry, providers, ct).ConfigureAwait(false);
        if (tool is null)
            return ToolNotFound(body.ToolCallId, body.ToolName, "not found");

        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        var agentCtx = new AgentContext(AgentName: agentId) { RunId = runId };
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()?.Push(agentCtx);

        // DefaultToolCallDispatcher gives us: IToolGuardrail Before/After hooks,
        // IAgentJournal append (when RunId is set), IAgentEventBus ToolCallStarted/Completed,
        // ToolGatewayMiddleware chain — same path C# agents use via StatefulAiAgent.
        var dispatcher = new DefaultToolCallDispatcher(
            toolRegistry:      new SingleToolRegistry(tool),
            toolGuardrails:    guardrails.ToArray(),
            eventBus:          ctx.RequestServices.GetService<IAgentEventBus>(),
            journal:           ctx.RequestServices.GetService<IAgentJournal>(),
            gatewayMiddleware: toolMiddleware);

        ToolCallOutcome outcome;
        try
        {
            outcome = await dispatcher.DispatchAsync(
                new ToolCallRequest(body.ToolName, body.Arguments, body.ToolCallId),
                agentCtx,
                ct).ConfigureAwait(false);
        }
        catch (AgentGuardrailDeniedException ex)
        {
            return Results.Ok(new GatewayToolInvokeResponse
            {
                ToolCallId = body.ToolCallId,
                Content    = $"Tool '{body.ToolName}' denied by guardrail: {ex.Message}",
                IsError    = true,
            });
        }

        return Results.Ok(new GatewayToolInvokeResponse
        {
            ToolCallId = body.ToolCallId,
            Content    = outcome.Result ?? outcome.Error ?? "",
            IsError    = outcome.Error is not null,
        });
    }

    private static IResult ToolNotFound(string toolCallId, string toolName, string reason) =>
        Results.Ok(new GatewayToolInvokeResponse
        {
            ToolCallId = toolCallId,
            Content    = $"Tool '{toolName}' not found: {reason}.",
            IsError    = true,
        });

    private static async Task<ITool?> FindToolAsync(
        string toolName,
        IMcpServerRegistry registry,
        IEnumerable<INamedToolSourceProvider> providers,
        CancellationToken ct)
    {
        await foreach (var server in registry.ListAsync(ct: ct).ConfigureAwait(false))
        {
            IToolSource? source = null;
            foreach (var p in providers)
            {
                source = p.GetByName(server.Id);
                if (source is not null) break;
            }
            if (source is null) continue;

            await foreach (var t in source.DiscoverAsync(ct).ConfigureAwait(false))
                if (string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase))
                    return t;
        }
        return null;
    }

    private sealed class SingleToolRegistry(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = [tool];

        public ITool? GetByName(string name)
            => string.Equals(name, tool.Name, StringComparison.OrdinalIgnoreCase) ? tool : null;
    }

    private static async Task<IResult> HandleToolsListAsync(
        IMcpServerRegistry? registry,
        IEnumerable<INamedToolSourceProvider> providers,
        CancellationToken ct)
    {
        if (registry is null)
            return Results.Ok(new GatewayToolListResponse());

        var tools = new List<GatewayToolInfo>();
        await foreach (var server in registry.ListAsync(ct: ct).ConfigureAwait(false))
        {
            IToolSource? source = null;
            foreach (var provider in providers)
            {
                source = provider.GetByName(server.Id);
                if (source is not null) break;
            }
            if (source is null) continue;

            await foreach (var tool in source.DiscoverAsync(ct).ConfigureAwait(false))
            {
                tools.Add(new GatewayToolInfo
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    ParametersSchema = tool.ParametersSchema,
                });
            }
        }

        return Results.Ok(new GatewayToolListResponse { Tools = tools });
    }

    private static async Task<IResult> HandleSectionsBuildAsync(
        HttpContext ctx,
        GatewaySectionsBuildRequest body,
        [FromServices] IAgentManifestTranslator translator,
        CancellationToken ct)
    {
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        var runId = ctx.Request.Headers["X-Run-Id"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(agentId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing X-Agent-Id header.");
        }

        StatefulAgentOptions options;
        try
        {
            options = await translator.TranslateAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (ManifestInstantiationException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: $"Agent '{agentId}' not found or not translatable.",
                detail: ex.Message);
        }

        var candidateTurns = body.Messages
            .Select(PluginMessageToChatTurn)
            .ToArray();

        var agentCtx = new AgentContext(AgentName: agentId) { RunId = runId };
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()?.Push(agentCtx);

        var sections = new List<Section>(capacity: candidateTurns.Length + 4);

        // 1. Composer (Section[]). The translator wires SystemPromptComposer from the manifest's
        //    systemPrompt + contributor chain. Null means "no composer for this agent" — skip.
        if (options.SystemPromptComposer is not null)
        {
            var composed = await options.SystemPromptComposer
                .ComposeSectionsAsync(agentCtx, ct).ConfigureAwait(false);
            sections.AddRange(composed);
        }
        else if (!string.IsNullOrEmpty(options.SystemPrompt))
        {
            sections.Add(new Section(
                "system.base",
                SectionKind.SystemSegment,
                new TextPayload(options.SystemPrompt),
                ProducerId: "Base"));
        }

        // 2. History/tools/format base sections — same shape StatefulAiAgent emits before the
        //    provider chain runs. The candidate the providers see (below) carries these too.
        foreach (var turn in candidateTurns)
        {
            var kind = turn.Role switch
            {
                AgentChatRole.User => SectionKind.UserMessage,
                AgentChatRole.Assistant => SectionKind.AssistantMessage,
                AgentChatRole.Tool => SectionKind.ToolMessage,
                AgentChatRole.System => SectionKind.SystemSegment,
                _ => SectionKind.UserMessage,
            };
            sections.Add(new Section(
                $"history.window.{sections.Count}",
                kind,
                kind == SectionKind.SystemSegment
                    ? (SectionPayload)new TextPayload(turn.Text ?? "")
                    : new TurnPayload(turn),
                ProducerId: "Base"));
        }

        var tools = options.ToolRegistry?.Tools;
        var hasTools = tools is { Count: > 0 };
        if (hasTools)
        {
            sections.Add(new Section(
                "tools.base",
                SectionKind.ToolDeclaration,
                new ToolsPayload(tools!),
                ProducerId: "Base"));
        }
        else if (options.ResponseFormat is not null)
        {
            sections.Add(new Section(
                "format.base",
                SectionKind.ResponseFormat,
                new ResponseFormatPayload(options.ResponseFormat),
                ProducerId: "Base"));
        }

        // 3. IContextProvider chain. Providers receive a snapshot of the candidate the plugin
        //    posted — matching the runtime-side contract where providers see the request as it
        //    stood before any provider contributed.
        if (options.ContextProviders.Count > 0)
        {
            var templateSystemPrompt = JoinSystemSegmentText(sections);
            var template = new CompletionRequest(
                candidateTurns,
                templateSystemPrompt,
                Tools: hasTools ? tools : null,
                ResponseFormat: hasTools ? null : options.ResponseFormat);

            var session = new InMemoryAgentSession(agentId, runId, candidateTurns);
            var invocation = new ContextInvocationContext(template, agentCtx, session);

            foreach (var provider in options.ContextProviders)
            {
                try
                {
                    var contribution = await provider
                        .InvokeAsync(invocation, ct).ConfigureAwait(false);
                    if (contribution.Sections.Count > 0)
                    {
                        sections.AddRange(contribution.Sections);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: $"Context provider '{provider.GetType().Name}' failed.",
                        detail: ex.Message,
                        extensions: new Dictionary<string, object?>
                        {
                            ["producerId"] = provider.GetType().Name,
                        });
                }
            }
        }

        // 4. Resolver (id uniqueness, kind+order canonicalisation). Packer is NOT run — plugin
        //    picks its own subset.
        var resolver = options.SectionResolver ?? DefaultSectionResolver.Instance;
        IReadOnlyList<Section> resolved;
        try
        {
            resolved = await resolver.ResolveAsync(sections, ct).ConfigureAwait(false);
        }
        catch (SectionCollisionException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Section id collision.",
                detail: ex.Message);
        }

        var wire = resolved.Select(SectionToWire).ToArray();
        var totalChars = wire.Sum(s => s.Payload.Value?.Length ?? s.Payload.Turn?.Content?.Length ?? 0);

        return Results.Ok(new GatewaySectionsBuildResponse
        {
            Sections = wire,
            TotalChars = totalChars,
        });
    }

    private static string? JoinSystemSegmentText(IEnumerable<Section> sections)
    {
        var parts = sections
            .Where(s => s.Kind == SectionKind.SystemSegment && s.Payload is TextPayload tp && tp.Value.Length > 0)
            .Select(s => ((TextPayload)s.Payload).Value)
            .ToArray();
        return parts.Length == 0 ? null : string.Join("\n\n", parts);
    }

    private static GatewaySection SectionToWire(Section s)
    {
        var payload = new GatewaySectionPayload();
        switch (s.Payload)
        {
            case TextPayload t:
                payload = new GatewaySectionPayload { Value = t.Value };
                break;
            case TurnPayload tp:
                payload = new GatewaySectionPayload { Turn = ChatTurnToPluginMessage(tp.Turn) };
                break;
            case ToolsPayload tools:
                payload = new GatewaySectionPayload
                {
                    Tools = tools.Tools.Select(t => new GatewayToolInfo
                    {
                        Name = t.Name,
                        Description = t.Description,
                        ParametersSchema = t.ParametersSchema,
                    }).ToArray(),
                };
                break;
            case ResponseFormatPayload rf:
                payload = new GatewaySectionPayload
                {
                    Spec = new GatewayResponseFormatSpec
                    {
                        Schema = rf.Spec.Schema,
                        Name = rf.Spec.SchemaName,
                        Strict = rf.Spec.Strict,
                    },
                };
                break;
            case MetadataPayload md:
                // Round-trip arbitrary values through JSON so the wire shape is well-typed.
                payload = new GatewaySectionPayload
                {
                    Values = md.Values.ToDictionary(
                        kv => kv.Key,
                        kv => JsonSerializer.SerializeToElement(kv.Value)),
                };
                break;
        }

        return new GatewaySection
        {
            Id = s.Id,
            Kind = s.Kind.ToString(),
            Payload = payload,
            Order = s.Order,
            ProducerId = s.ProducerId,
            Budget = s.Budget is null ? null : new GatewaySectionBudget
            {
                Priority = s.Budget.Priority,
                MaxChars = s.Budget.MaxChars,
            },
        };
    }

    private static PluginMessage ChatTurnToPluginMessage(ChatTurn turn) => new()
    {
        Role = turn.Role switch
        {
            AgentChatRole.System => "system",
            AgentChatRole.Assistant => "assistant",
            AgentChatRole.Tool => "tool",
            _ => "user",
        },
        Content = turn.Text,
        ToolCallId = turn.ToolCallId,
        ToolCalls = turn.ToolCalls?
            .Select(tc => new PluginToolCall { Id = tc.CallId, Name = tc.ToolName, Arguments = tc.Arguments })
            .ToArray(),
    };

    private static ChatTurn OpenAiMessageToChatTurn(OpenAiChatMessage msg)
    {
        var role = msg.Role switch
        {
            "system"    => AgentChatRole.System,
            "assistant" => AgentChatRole.Assistant,
            "tool"      => AgentChatRole.Tool,
            _           => AgentChatRole.User,
        };

        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (msg.ToolCalls is { Count: > 0 } tcs)
        {
            toolCalls = tcs.Select(tc =>
            {
                // OpenAI sends function.arguments as a JSON-encoded string; parse so downstream
                // providers receive structured JsonElement values rather than an opaque string.
                JsonElement args;
                if (string.IsNullOrEmpty(tc.Function.Arguments))
                {
                    using var empty = JsonDocument.Parse("{}");
                    args = empty.RootElement.Clone();
                }
                else
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(tc.Function.Arguments);
                        args = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        using var fallback = JsonDocument.Parse("{}");
                        args = fallback.RootElement.Clone();
                    }
                }
                return new ToolCallRequest(tc.Function.Name, args, tc.Id);
            }).ToArray();
        }

        return new ChatTurn(role, msg.Content ?? "", ToolCalls: toolCalls, ToolCallId: msg.ToolCallId);
    }

    private static ChatTurn PluginMessageToChatTurn(PluginMessage msg)
    {
        var role = msg.Role switch
        {
            "system" => AgentChatRole.System,
            "user" => AgentChatRole.User,
            "assistant" => AgentChatRole.Assistant,
            "tool" => AgentChatRole.Tool,
            _ => AgentChatRole.User,
        };

        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (msg.ToolCalls is { Count: > 0 } tcs)
        {
            toolCalls = tcs.Select(tc => new ToolCallRequest(tc.Name, tc.Arguments, tc.Id)).ToArray();
        }

        return new ChatTurn(role, msg.Content ?? "", ToolCalls: toolCalls, ToolCallId: msg.ToolCallId);
    }
}
