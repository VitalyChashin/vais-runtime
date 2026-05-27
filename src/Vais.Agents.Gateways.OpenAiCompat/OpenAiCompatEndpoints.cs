// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Vais.Agents.Gateways.OpenAiCompat.Models;

namespace Vais.Agents.Gateways.OpenAiCompat;

/// <summary>
/// Registers the OpenAI-compatible gateway endpoints onto an
/// <see cref="IEndpointRouteBuilder"/>. Call from the application host's
/// <c>Configure</c> phase: <c>app.MapOpenAiCompat()</c>.
/// </summary>
public static class OpenAiCompatEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Maps <c>POST /v1/chat/completions</c> and <c>GET /v1/models</c>.
    /// </summary>
    /// <remarks>
    /// <b>Routing.</b> Model IDs prefixed <c>agent:</c> route to
    /// <see cref="IAgentLifecycleManager"/>; <c>graph:</c> route to
    /// <see cref="IAgentGraphLifecycleManager"/>; anything else falls through to the
    /// existing <see cref="IModelRouter"/> LLM gateway.<br/>
    /// <b>Streaming.</b> Requests with <c>stream: true</c> require the registered
    /// <see cref="ICompletionProvider"/> to implement <see cref="IStreamingCompletionProvider"/>
    /// for LLM models. Agent streaming uses <see cref="IAgentRuntime"/>.<br/>
    /// <b>Run correlation.</b> An optional inbound <c>X-Run-Id</c> header sets the run id for the
    /// call. On the LLM path it stamps <see cref="AgentContext.RunId"/> so a multi-turn client's
    /// completions group under one run in telemetry. On the <c>agent:</c>/<c>graph:</c> paths it is
    /// used as the session / run id, giving multi-call session continuity. When absent, the run id
    /// is identity-derived (from <see cref="IInboundIdentityResolver"/>) or minted per call. An
    /// explicit header overrides an identity-derived run id.
    /// </remarks>
    public static IEndpointRouteBuilder MapOpenAiCompat(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1");
        group.MapPost("/chat/completions", HandleChatCompletionAsync);
        group.MapGet("/models", HandleModelsAsync);
        return app;
    }

    // ── /v1/chat/completions ─────────────────────────────────────────────────

    private static async Task HandleChatCompletionAsync(
        HttpContext ctx,
        IInboundIdentityResolver identityResolver,
        IModelRouter modelRouter,
        IAgentContextSetter contextSetter,
        IEnumerable<LlmGatewayMiddleware> gatewayMiddleware,
        IOptions<OpenAiCompatOptions> options,
        CancellationToken ct)
    {
        // 1. Deserialize request body
        ChatCompletionRequest? oaiRequest;
        try
        {
            oaiRequest = await ctx.Request.ReadFromJsonAsync<ChatCompletionRequest>(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest,
                "invalid_request_error", "Request body could not be parsed as JSON.", ct).ConfigureAwait(false);
            return;
        }

        if (oaiRequest is null || string.IsNullOrWhiteSpace(oaiRequest.Model))
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest,
                "invalid_request_error", "Field 'model' is required.", ct).ConfigureAwait(false);
            return;
        }

        // 2. Resolve identity
        AgentContext agentCtx;
        try
        {
            var bearer = ExtractBearer(ctx.Request.Headers.Authorization);
            agentCtx = await identityResolver.ResolveAsync(bearer, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status401Unauthorized,
                "invalid_api_key", ex.Message, ct).ConfigureAwait(false);
            return;
        }

        // 2b. Caller-supplied run correlation — an inbound X-Run-Id overrides the identity-derived
        // run id and groups this call with the rest of the caller's session in telemetry.
        var inboundRunId = ResolveInboundRunId(ctx.Request);
        if (inboundRunId is not null)
        {
            agentCtx = agentCtx with
            {
                RunId = inboundRunId,
                CorrelationId = agentCtx.CorrelationId ?? inboundRunId
            };
        }

        // 2c. G5 in-process Budget propagation — for `agent:foo` requests, overlay the agent's
        // manifest-declared RunBudget onto the context BEFORE the push so LlmGatewayMiddleware
        // (whether on the AsyncLocal scope or downstream via Orleans propagation) sees the same
        // Budget the container-plugin gateway already sees. Non-agent model requests stay null.
        // IAgentRegistry resolved via ctx.RequestServices (not as a handler parameter) to avoid
        // ASP.NET minimal-API confusing it for a body-bound type in test rigs that don't register it.
        if (oaiRequest.Model.StartsWith("agent:", StringComparison.Ordinal))
        {
            var registry = ctx.RequestServices.GetService<IAgentRegistry>();
            if (registry is not null)
            {
                var agentIdForBudget = oaiRequest.Model["agent:".Length..];
                try
                {
                    var manifest = await registry.GetAsync(agentIdForBudget, version: null, ct).ConfigureAwait(false);
                    if (manifest?.Budget is { } budget)
                        agentCtx = agentCtx with { Budget = budget };
                }
                catch
                {
                    // Defensive: registry lookup failure shouldn't abort the request. Downstream
                    // agent dispatch will surface a real error if the agent truly doesn't exist.
                }
            }
        }

        using var _ = contextSetter.Push(agentCtx);

        // 3. Routing fork — agent: and graph: bypass the LLM model router
        if (oaiRequest.Model.StartsWith("agent:", StringComparison.Ordinal))
        {
            if (!options.Value.AgentRoutingEnabled)
            {
                await WriteRoutingDisabledAsync(ctx, "agent", ct).ConfigureAwait(false);
                return;
            }
            var agentId = oaiRequest.Model["agent:".Length..];
            if (oaiRequest.Stream == true)
                await HandleAgentStreamAsync(ctx, agentId, oaiRequest, agentCtx, ct).ConfigureAwait(false);
            else
                await HandleAgentCompletionAsync(ctx, agentId, oaiRequest, agentCtx, ct).ConfigureAwait(false);
            return;
        }

        if (oaiRequest.Model.StartsWith("graph:", StringComparison.Ordinal))
        {
            if (!options.Value.GraphRoutingEnabled)
            {
                await WriteRoutingDisabledAsync(ctx, "graph", ct).ConfigureAwait(false);
                return;
            }
            var graphId = oaiRequest.Model["graph:".Length..];
            if (oaiRequest.Stream == true)
                await HandleGraphStreamAsync(ctx, graphId, oaiRequest, agentCtx, ct).ConfigureAwait(false);
            else
                await HandleGraphCompletionAsync(ctx, graphId, oaiRequest, agentCtx, ct).ConfigureAwait(false);
            return;
        }

        // 4. Resolve model route (LLM gateway path)
        ModelRoute route;
        try
        {
            route = await modelRouter.ResolveAsync(oaiRequest.Model, ct).ConfigureAwait(false);
        }
        catch (ModelNotFoundException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound,
                "model_not_found", ex.Message, ct).ConfigureAwait(false);
            return;
        }

        // 5. Translate request and execute chain
        var request = OpenAiTranslator.ToCompletionRequest(oaiRequest);
        var middleware = gatewayMiddleware.ToArray();
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        try
        {
            if (oaiRequest.Stream == true)
            {
                if (route.Provider is not IStreamingCompletionProvider streamingProvider)
                {
                    await WriteErrorAsync(ctx.Response, StatusCodes.Status422UnprocessableEntity,
                        "streaming_not_supported",
                        $"Provider for model '{oaiRequest.Model}' does not support streaming.", ct).ConfigureAwait(false);
                    return;
                }

                var stream = LlmGatewayPipeline.StreamAsync(request, streamingProvider, middleware, ct);
                await OpenAiSseWriter.WriteStreamAsync(ctx.Response, completionId, oaiRequest.Model, stream, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var response = await LlmGatewayPipeline.InvokeAsync(request, route.Provider, middleware, ct)
                    .ConfigureAwait(false);
                var oaiResponse = OpenAiTranslator.ToChatCompletionResponse(response, oaiRequest.Model, completionId);
                await ctx.Response.WriteAsJsonAsync(oaiResponse, ct).ConfigureAwait(false);
            }
        }
        catch (AgentBudgetExceededException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status429TooManyRequests,
                "rate_limit_exceeded", ex.Message, ct).ConfigureAwait(false);
        }
        catch (AgentGuardrailDeniedException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest,
                "content_policy_violation", ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status500InternalServerError,
                "server_error", ex.Message, ct).ConfigureAwait(false);
        }
    }

    // ── /v1/models ───────────────────────────────────────────────────────────

    private static async Task HandleModelsAsync(
        HttpContext ctx,
        IModelRouter modelRouter,
        IOptions<OpenAiCompatOptions> options,
        CancellationToken ct)
    {
        var aliases = await modelRouter.ListAliasesAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var models = new List<ModelObject>(aliases.Select(alias => new ModelObject
        {
            Id = alias,
            Created = now,
            OwnedBy = "vais"
        }));

        // Append agent models (skipped when AgentRoutingEnabled = false)
        if (options.Value.AgentRoutingEnabled)
        {
            var agentRegistry = ctx.RequestServices.GetService<IAgentRegistry>();
            if (agentRegistry is not null)
            {
                await foreach (var manifest in agentRegistry.ListAsync(null, ct).ConfigureAwait(false))
                {
                    models.Add(new ModelObject
                    {
                        Id = $"agent:{manifest.Id}",
                        Created = now,
                        OwnedBy = "vais-agent"
                    });
                }
            }
        }

        // Append graph models — only those with the OpenAI-compat input annotation (skipped when GraphRoutingEnabled = false)
        if (options.Value.GraphRoutingEnabled)
        {
            var graphRegistry = ctx.RequestServices.GetService<IAgentGraphRegistry>();
            if (graphRegistry is not null)
            {
                await foreach (var manifest in graphRegistry.ListAsync(null, ct).ConfigureAwait(false))
                {
                    if (manifest.Annotations?.ContainsKey("vais.io/openai-compat-input-key") == true)
                    {
                        models.Add(new ModelObject
                        {
                            Id = $"graph:{manifest.Id}",
                            Created = now,
                            OwnedBy = "vais-graph"
                        });
                    }
                }
            }
        }

        var list = new ModelListResponse { Data = models };
        await ctx.Response.WriteAsJsonAsync(list, ct).ConfigureAwait(false);
    }

    // ── Agent dispatch — non-streaming (OC-6) ────────────────────────────────

    private static async Task HandleAgentCompletionAsync(
        HttpContext ctx,
        string agentId,
        ChatCompletionRequest oaiRequest,
        AgentContext agentCtx,
        CancellationToken ct)
    {
        var agentRegistry = ctx.RequestServices.GetService<IAgentRegistry>();
        var lifecycleManager = ctx.RequestServices.GetService<IAgentLifecycleManager>();

        if (agentRegistry is null || lifecycleManager is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                $"Agent routing is not configured. Register IAgentRegistry and IAgentLifecycleManager.", ct).ConfigureAwait(false);
            return;
        }

        var manifest = await agentRegistry.GetAsync(agentId, null, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                $"Agent '{agentId}' not found.", ct).ConfigureAwait(false);
            return;
        }

        // Find last user message
        var messages = oaiRequest.Messages;
        var lastUserIndex = FindLastUserMessageIndex(messages);
        if (lastUserIndex < 0)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "invalid_request_error",
                "No user message found in messages.", ct).ConfigureAwait(false);
            return;
        }

        var lastUserText = messages[lastUserIndex].Content ?? "";
        var history = BuildInitialHistory(messages, lastUserIndex);
        var metadata = BuildCallerMetadata(oaiRequest);

        var invocationRequest = new AgentInvocationRequest(
            Text: lastUserText,
            SessionId: agentCtx.RunId ?? Guid.NewGuid().ToString("N"),
            Metadata: metadata.Count > 0 ? metadata : null,
            InitialHistory: history.Count > 0 ? history : null);

        var handle = new AgentHandle(manifest.Id, manifest.Version);

        AgentInvocationResult result;
        try
        {
            result = await lifecycleManager.InvokeAsync(handle, invocationRequest, ct).ConfigureAwait(false);
        }
        catch (AgentBudgetExceededException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status429TooManyRequests, "rate_limit_exceeded", ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (AgentGuardrailDeniedException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "content_policy_violation", ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status500InternalServerError, "server_error", ex.Message, ct).ConfigureAwait(false);
            return;
        }

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var promptTokens = TryGetInt(result.Metadata, "prompt_tokens");
        var completionTokens = TryGetInt(result.Metadata, "completion_tokens");

        var oaiResponse = new ChatCompletionResponse
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = oaiRequest.Model,
            Choices =
            [
                new ChatCompletionChoice
                {
                    Index = 0,
                    Message = new ChatMessage { Role = "assistant", Content = result.Text },
                    FinishReason = "stop"
                }
            ],
            Usage = new ChatUsage
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = promptTokens + completionTokens
            }
        };

        await ctx.Response.WriteAsJsonAsync(oaiResponse, ct).ConfigureAwait(false);
    }

    // ── Agent dispatch — streaming (OC-7) ────────────────────────────────────

    private static async Task HandleAgentStreamAsync(
        HttpContext ctx,
        string agentId,
        ChatCompletionRequest oaiRequest,
        AgentContext agentCtx,
        CancellationToken ct)
    {
        var agentRuntime = ctx.RequestServices.GetService<IAgentRuntime>();

        if (agentRuntime is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                "Agent streaming is not configured. Register IAgentRuntime to enable agent streaming.", ct).ConfigureAwait(false);
            return;
        }

        IAiAgent agent;
        try
        {
            agent = agentCtx.RunId is { } sessionId
                ? agentRuntime.GetOrCreateForSession(agentId, sessionId)
                : agentRuntime.GetOrCreate(agentId);
        }
        catch
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                $"Agent '{agentId}' not found.", ct).ConfigureAwait(false);
            return;
        }

        // Find last user message
        var messages = oaiRequest.Messages;
        var lastUserIndex = FindLastUserMessageIndex(messages);
        if (lastUserIndex < 0)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "invalid_request_error",
                "No user message found in messages.", ct).ConfigureAwait(false);
            return;
        }

        var lastUserText = messages[lastUserIndex].Content ?? "";
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        // Fallback: non-streaming agent → emit as single SSE response
        if (agent is not IStreamingAiAgent streamingAgent)
        {
            string text;
            try { text = await agent.AskAsync(lastUserText, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await WriteErrorAsync(ctx.Response, StatusCodes.Status500InternalServerError, "server_error", ex.Message, ct).ConfigureAwait(false);
                return;
            }

            await WriteSingleChunkSseAsync(ctx.Response, completionId, oaiRequest.Model, text, ct).ConfigureAwait(false);
            return;
        }

        // Streaming path
        var events = streamingAgent.StreamAsync(lastUserText, agentCtx, ct);
        await WriteAgentEventSseAsync(ctx.Response, completionId, oaiRequest.Model, events, ct).ConfigureAwait(false);
    }

    // ── Graph dispatch — non-streaming (OC-8) ────────────────────────────────

    private static async Task HandleGraphCompletionAsync(
        HttpContext ctx,
        string graphId,
        ChatCompletionRequest oaiRequest,
        AgentContext agentCtx,
        CancellationToken ct)
    {
        var graphRegistry = ctx.RequestServices.GetService<IAgentGraphRegistry>();
        var graphLifecycleManager = ctx.RequestServices.GetService<IAgentGraphLifecycleManager>();

        if (graphRegistry is null || graphLifecycleManager is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                "Graph routing is not configured. Register IAgentGraphRegistry and IAgentGraphLifecycleManager.", ct).ConfigureAwait(false);
            return;
        }

        var manifest = await graphRegistry.GetAsync(graphId, null, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                $"Graph '{graphId}' not found.", ct).ConfigureAwait(false);
            return;
        }

        if (manifest.Annotations is null ||
            !manifest.Annotations.TryGetValue("vais.io/openai-compat-input-key", out var inputKey))
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status422UnprocessableEntity, "unprocessable_entity",
                $"Graph '{graphId}' is missing the 'vais.io/openai-compat-input-key' annotation.", ct).ConfigureAwait(false);
            return;
        }

        manifest.Annotations.TryGetValue("vais.io/openai-compat-output-key", out var outputKey);
        outputKey ??= inputKey;

        var lastUserIndex = FindLastUserMessageIndex(oaiRequest.Messages);
        if (lastUserIndex < 0)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "invalid_request_error",
                "No user message found in messages.", ct).ConfigureAwait(false);
            return;
        }
        var lastUserText = oaiRequest.Messages[lastUserIndex].Content ?? "";
        var inputJson = JsonSerializer.SerializeToElement(lastUserText, SseJsonOptions);
        var metadata = BuildCallerMetadata(oaiRequest);

        var graphRequest = new GraphInvocationRequest(
            InitialState: new Dictionary<string, JsonElement> { [inputKey] = inputJson },
            Metadata: metadata.Count > 0 ? metadata : null,
            RunId: agentCtx.RunId ?? Guid.NewGuid().ToString("N"));

        var handle = new AgentGraphHandle(manifest.Id, manifest.Version);

        GraphInvocationResult result;
        try
        {
            result = await graphLifecycleManager.InvokeAsync(handle, graphRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status500InternalServerError, "server_error", ex.Message, ct).ConfigureAwait(false);
            return;
        }

        string? outputContent = ExtractGraphOutput(result.FinalState, outputKey);
        if (outputContent is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status500InternalServerError, "server_error",
                $"Graph completed but output key '{outputKey}' not found in state.", ct).ConfigureAwait(false);
            return;
        }

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var oaiResponse = new ChatCompletionResponse
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = oaiRequest.Model,
            Choices =
            [
                new ChatCompletionChoice
                {
                    Index = 0,
                    Message = new ChatMessage { Role = "assistant", Content = outputContent },
                    FinishReason = "stop"
                }
            ],
            Usage = new ChatUsage { PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0 }
        };

        await ctx.Response.WriteAsJsonAsync(oaiResponse, ct).ConfigureAwait(false);
    }

    // ── Graph dispatch — streaming (OC-9) ────────────────────────────────────

    private static async Task HandleGraphStreamAsync(
        HttpContext ctx,
        string graphId,
        ChatCompletionRequest oaiRequest,
        AgentContext agentCtx,
        CancellationToken ct)
    {
        var graphRegistry = ctx.RequestServices.GetService<IAgentGraphRegistry>();
        var graphLifecycleManager = ctx.RequestServices.GetService<IAgentGraphLifecycleManager>();

        if (graphRegistry is null || graphLifecycleManager is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                "Graph routing is not configured.", ct).ConfigureAwait(false);
            return;
        }

        var manifest = await graphRegistry.GetAsync(graphId, null, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound, "model_not_found",
                $"Graph '{graphId}' not found.", ct).ConfigureAwait(false);
            return;
        }

        if (manifest.Annotations is null ||
            !manifest.Annotations.TryGetValue("vais.io/openai-compat-input-key", out var inputKey))
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status422UnprocessableEntity, "unprocessable_entity",
                $"Graph '{graphId}' is missing the 'vais.io/openai-compat-input-key' annotation.", ct).ConfigureAwait(false);
            return;
        }

        manifest.Annotations.TryGetValue("vais.io/openai-compat-output-key", out var streamOutputKey);
        streamOutputKey ??= inputKey;

        // Collect all nodes whose output binding declares outputKey (multiple in a branching graph).
        // Their NodeAgentInvoked.OutputText is the raw LLM response; we suppress it and show only
        // FinalState[outputKey] from GraphCompleted as the definitive final answer.
        var outputNodeIds = manifest.Nodes
            .Where(n => n.StateBindings?.Output?.Contains(streamOutputKey, StringComparer.Ordinal) == true)
            .Select(static n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var lastUserIndex = FindLastUserMessageIndex(oaiRequest.Messages);
        if (lastUserIndex < 0)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "invalid_request_error",
                "No user message found in messages.", ct).ConfigureAwait(false);
            return;
        }
        var lastUserText = oaiRequest.Messages[lastUserIndex].Content ?? "";
        var inputJson = JsonSerializer.SerializeToElement(lastUserText, SseJsonOptions);
        var metadata = BuildCallerMetadata(oaiRequest);

        var graphRequest = new GraphInvocationRequest(
            InitialState: new Dictionary<string, JsonElement> { [inputKey] = inputJson },
            Metadata: metadata.Count > 0 ? metadata : null,
            RunId: agentCtx.RunId ?? Guid.NewGuid().ToString("N"));

        var handle = new AgentGraphHandle(manifest.Id, manifest.Version);
        var events = graphLifecycleManager.InvokeStreamAsync(handle, graphRequest, ct);
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        await WriteGraphEventSseAsync(ctx.Response, completionId, oaiRequest.Model, streamOutputKey, outputNodeIds, events, ct).ConfigureAwait(false);
    }

    // ── SSE helpers ──────────────────────────────────────────────────────────

    private static async Task WriteAgentEventSseAsync(
        HttpResponse response,
        string completionId,
        string model,
        IAsyncEnumerable<AgentEvent> events,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        // Role header chunk
        await WriteSseChunkAsync(response, completionId, model, delta: new ChatDelta { Role = "assistant" }, finishReason: null, ct).ConfigureAwait(false);

        await foreach (var evt in events.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case CompletionDelta d when d.TextDelta.Length > 0:
                    await WriteSseChunkAsync(response, completionId, model,
                        new ChatDelta { Content = d.TextDelta }, null, ct).ConfigureAwait(false);
                    break;

                case GuardrailTriggered g when g.Reason is not null:
                    await WriteSseChunkAsync(response, completionId, model,
                        new ChatDelta { Content = g.Reason }, null, ct).ConfigureAwait(false);
                    break;

                case TurnCompleted:
                    await WriteSseChunkAsync(response, completionId, model, new ChatDelta(), "stop", ct).ConfigureAwait(false);
                    await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    return;

                case TurnFailed f:
                    await WriteSseChunkAsync(response, completionId, model,
                        new ChatDelta { Content = f.ErrorMessage }, "stop", ct).ConfigureAwait(false);
                    await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    return;
            }
        }

        // Stream ended without TurnCompleted — emit stop sentinel
        await WriteSseChunkAsync(response, completionId, model, new ChatDelta(), "stop", ct).ConfigureAwait(false);
        await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteGraphEventSseAsync(
        HttpResponse response,
        string completionId,
        string model,
        string outputKey,
        HashSet<string> outputNodeIds,
        IAsyncEnumerable<AgentGraphEvent> events,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        // Role header chunk
        await WriteSseChunkAsync(response, completionId, model, new ChatDelta { Role = "assistant" }, null, ct).ConfigureAwait(false);

        var firstOutput = true;

        await foreach (var evt in events.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                // Only emit progress markers for agent-kind nodes; End/Interrupt/Code nodes
                // produce no visible work and their markers confuse readers.
                case NodeStarted n when string.Equals(n.NodeKind, "Agent", StringComparison.Ordinal):
                    await WriteSseChunkAsync(response, completionId, model,
                        new ChatDelta { Content = $"*[{n.NodeId} running...]*\n\n" }, null, ct).ConfigureAwait(false);
                    break;

                // Suppress any output-node's raw LLM text — it may be a partial or malformed
                // response. The definitive answer is always FinalState[outputKey] from GraphCompleted.
                case NodeAgentInvoked n when n.OutputText.Length > 0
                                          && !outputNodeIds.Contains(n.NodeId):
                    if (!firstOutput)
                    {
                        await WriteSseChunkAsync(response, completionId, model,
                            new ChatDelta { Content = "\n\n---\n\n" }, null, ct).ConfigureAwait(false);
                    }
                    await WriteSseChunkAsync(response, completionId, model,
                        new ChatDelta { Content = n.OutputText }, null, ct).ConfigureAwait(false);
                    firstOutput = false;
                    break;

                case GraphCompleted g:
                {
                    if (g.FinalState is not null)
                    {
                        var finalContent = ExtractGraphOutput(g.FinalState, outputKey);
                        if (!string.IsNullOrWhiteSpace(finalContent))
                        {
                            if (!firstOutput)
                            {
                                await WriteSseChunkAsync(response, completionId, model,
                                    new ChatDelta { Content = "\n\n---\n\n" }, null, ct).ConfigureAwait(false);
                            }
                            await WriteSseChunkAsync(response, completionId, model,
                                new ChatDelta { Content = finalContent }, null, ct).ConfigureAwait(false);
                        }
                    }
                    await WriteSseChunkAsync(response, completionId, model, new ChatDelta(), "stop", ct).ConfigureAwait(false);
                    await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    return;
                }

                case GraphFailed f:
                    await WriteSseChunkAsync(response, completionId, model,
                        new ChatDelta { Content = $"{f.ErrorType}: {f.ErrorMessage}" }, "stop", ct).ConfigureAwait(false);
                    await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    return;
            }
        }

        // Stream ended without GraphCompleted
        await WriteSseChunkAsync(response, completionId, model, new ChatDelta(), "stop", ct).ConfigureAwait(false);
        await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSingleChunkSseAsync(
        HttpResponse response,
        string completionId,
        string model,
        string content,
        CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        await WriteSseChunkAsync(response, completionId, model, new ChatDelta { Role = "assistant" }, null, ct).ConfigureAwait(false);
        await WriteSseChunkAsync(response, completionId, model, new ChatDelta { Content = content }, null, ct).ConfigureAwait(false);
        await WriteSseChunkAsync(response, completionId, model, new ChatDelta(), "stop", ct).ConfigureAwait(false);
        await response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSseChunkAsync(
        HttpResponse response,
        string completionId,
        string model,
        ChatDelta delta,
        string? finishReason,
        CancellationToken ct)
    {
        var chunk = new ChatCompletionChunk
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = delta,
                    FinishReason = finishReason
                }
            ]
        };
        var json = JsonSerializer.Serialize(chunk, SseJsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static int FindLastUserMessageIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(messages[i].Content))
                return i;
        }
        return -1;
    }

    private static IReadOnlyList<(string Role, string Content)> BuildInitialHistory(
        IReadOnlyList<ChatMessage> messages,
        int lastUserIndex)
    {
        var history = new List<(string, string)>(lastUserIndex);
        for (var i = 0; i < lastUserIndex; i++)
        {
            var msg = messages[i];
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase)) continue;
            if (msg.Content is null) continue;
            history.Add((msg.Role, msg.Content));
        }
        return history;
    }

    private static Dictionary<string, string> BuildCallerMetadata(ChatCompletionRequest oaiRequest)
    {
        var meta = new Dictionary<string, string>();
        if (oaiRequest.Temperature.HasValue)
            meta["oai.temperature"] = oaiRequest.Temperature.Value.ToString(CultureInfo.InvariantCulture);
        if (oaiRequest.MaxTokens.HasValue)
            meta["oai.max_tokens"] = oaiRequest.MaxTokens.Value.ToString(CultureInfo.InvariantCulture);
        if (oaiRequest.Tools is { Count: > 0 })
            meta["oai.tools"] = JsonSerializer.Serialize(oaiRequest.Tools);
        if (oaiRequest.ToolChoice is not null)
            meta["oai.tool_choice"] = oaiRequest.ToolChoice;
        return meta;
    }

    private static string? ExtractGraphOutput(IDictionary<string, JsonElement> finalState, string outputKey)
        => ExtractGraphOutput((IReadOnlyDictionary<string, JsonElement>)finalState, outputKey);

    private static string? ExtractGraphOutput(IReadOnlyDictionary<string, JsonElement> finalState, string outputKey)
    {
        if (!finalState.TryGetValue(outputKey, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Array)
        {
            // Find last assistant message in the array
            foreach (var item in el.EnumerateArray().Reverse())
            {
                if (item.TryGetProperty("role", out var roleEl) &&
                    string.Equals(roleEl.GetString(), "assistant", StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("content", out var contentEl))
                {
                    return contentEl.GetString();
                }
            }
        }

        return null;
    }

    private static int TryGetInt(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        if (metadata is not null &&
            metadata.TryGetValue(key, out var val) &&
            int.TryParse(val, out var result))
            return result;
        return 0;
    }

    // Reads the optional inbound X-Run-Id header. Returns a sanitized token, or null when absent
    // or invalid (too long, or containing control/whitespace chars) — callers then fall back to
    // the identity-derived run id or a per-call mint.
    private static string? ResolveInboundRunId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("X-Run-Id", out var values) || values.Count == 0)
            return null;

        var raw = values[^1];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.Length > 200)
            return null;

        foreach (var c in trimmed)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return null;
        }

        return trimmed;
    }

    private static string ExtractBearer(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return "";

        if (AuthenticationHeaderValue.TryParse(authHeader, out var parsed) &&
            string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return parsed.Parameter ?? "";
        }

        return "";
    }

    private static Task WriteRoutingDisabledAsync(HttpContext ctx, string routingType, CancellationToken ct)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return ctx.Response.WriteAsJsonAsync(
            new { error = new { message = $"{routingType} routing is disabled by configuration." } },
            ct);
    }

    private static Task WriteErrorAsync(
        HttpResponse response,
        int statusCode,
        string type,
        string message,
        CancellationToken ct)
    {
        if (!response.HasStarted)
            response.StatusCode = statusCode;

        var error = new ChatErrorResponse
        {
            Error = new ChatError { Message = message, Type = type }
        };
        return response.WriteAsJsonAsync(error, ct);
    }
}
