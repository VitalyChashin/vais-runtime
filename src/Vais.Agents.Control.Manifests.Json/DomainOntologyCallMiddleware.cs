// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Request-phase south cartridge middleware. Validates the tool-call arguments against the
/// bound <see cref="IDomainOntologyCatalog"/>'s typed cross-refs: every cross-ref's
/// <c>FieldPath</c> must resolve to a non-empty value in the args, otherwise the call
/// short-circuits with <c>{ok:false, reason, suggestions[]}</c> before the upstream tool is
/// invoked. Pass-through for tools outside the catalog scope.
/// </summary>
/// <remarks>
/// Declares <see cref="InterceptorKind.Validation"/>: this middleware must not mutate the
/// arguments and must not produce side effects beyond a synthetic outcome. Plan C1-11.
/// </remarks>
public sealed class DomainOntologyArgValidationMiddleware(IDomainOntologyCatalog catalog) : ToolGatewayMiddleware
{
    private readonly IDomainOntologyCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public override InterceptorKind Kind => InterceptorKind.Validation;

    /// <inheritdoc />
    public override Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        if (!_catalog.TryGetConcept(context.ToolName, out var concept)) return next();
        if (concept.CrossRefs.Count == 0) return next();

        var missing = new List<string>();
        foreach (var xref in concept.CrossRefs)
        {
            if (!TryResolvePath(context.Arguments, xref.FieldPath, out var value) || IsEmpty(value))
            {
                missing.Add(xref.FieldPath);
            }
        }
        if (missing.Count == 0) return next();

        var suggestions = new JsonArray();
        foreach (var fieldPath in missing)
            suggestions.Add($"provide a value for argument '{fieldPath}'");
        var payload = new JsonObject
        {
            ["ok"] = false,
            ["reason"] = $"missing required argument(s) referenced by domain ontology: {string.Join(", ", missing)}",
            ["suggestions"] = suggestions,
        };
        return Task.FromResult(new ToolCallOutcome(context.CallId, payload.ToJsonString()));
    }

    private static bool TryResolvePath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.Array) return false;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var cursor = root;
        foreach (var seg in segments)
        {
            if (cursor.ValueKind != JsonValueKind.Object) { value = default; return false; }
            if (!cursor.TryGetProperty(seg, out var next)) { value = default; return false; }
            cursor = next;
        }
        value = cursor;
        return true;
    }

    private static bool IsEmpty(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.String => string.IsNullOrEmpty(el.GetString()),
        JsonValueKind.Array => el.GetArrayLength() == 0,
        JsonValueKind.Object => !el.EnumerateObject().Any(),
        _ => false,
    };
}

/// <summary>
/// Observability south cartridge middleware. Emits an <see cref="InterceptorTeeEvent"/>
/// per south tool call so any registered <see cref="IInterceptorTee"/> consumer
/// (Plan D <c>RecordingInterceptorTee</c>, deployer-side OTLP exporters, …) can record the
/// trajectory. Read-only with respect to the chain — pass-through when no tee is registered.
/// </summary>
/// <remarks>
/// Declares <see cref="InterceptorKind.Observability"/>. The payload is the
/// <c>ToolCallTrajectoryPayload</c> shape Plan D's adapter expects:
/// <c>(ConceptName, Transport: "south", Arguments, Outcome, Duration)</c>. PII redaction
/// happens at the adapter, not here — this middleware passes the raw arguments through and
/// trusts the adapter's redactor (Plan D §"raw values NEVER persist" invariant).
/// </remarks>
public sealed class DomainOntologyTraceMiddleware(IInterceptorTee tee, IDomainOntologyCatalog catalog) : ToolGatewayMiddleware
{
    private readonly IInterceptorTee _tee = tee ?? throw new ArgumentNullException(nameof(tee));
    private readonly IDomainOntologyCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public override InterceptorKind Kind => InterceptorKind.Observability;

    /// <inheritdoc />
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        ToolCallOutcome outcome;
        try
        {
            outcome = await next().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: emit a failure event then re-throw so downstream observability
            // still sees the call.
            var failureDuration = DateTimeOffset.UtcNow - start;
            var failureOutcome = new TrajectoryOutcome(TrajectoryOutcomeKind.Error);
            await EmitAsync(context, failureOutcome, failureDuration, cancellationToken).ConfigureAwait(false);
            throw;
        }
        var duration = DateTimeOffset.UtcNow - start;
        var trajectoryOutcome = outcome.Error is { Length: > 0 } err
            ? new TrajectoryOutcome(TrajectoryOutcomeKind.Error, err)
            : new TrajectoryOutcome(TrajectoryOutcomeKind.Ok);
        await EmitAsync(context, trajectoryOutcome, duration, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private async ValueTask EmitAsync(ToolGatewayContext context, TrajectoryOutcome outcome, TimeSpan duration, CancellationToken cancellationToken)
    {
        var payload = new ToolCallTrajectoryPayload(
            ConceptName: context.ToolName,
            Transport: "south",
            Arguments: context.Arguments,
            Outcome: outcome,
            Duration: duration);
        var evt = new InterceptorTeeEvent
        {
            EventName = "tool.call",
            Context = new SouthToolCallInterceptionContext
            {
                Operation = OntologyOperation.Call,
                AgentContext = context.AgentContext,
                Binding = _catalog,
            },
            Payload = payload,
        };
        await _tee.EmitAsync(evt, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Concrete <see cref="InterceptionContext"/> for south tool-call observations. Carries the
/// AgentContext + the bound domain ontology so the recording adapter can read ontology
/// version + identity. The substrate intentionally doesn't ship a south-flavored
/// InterceptionContext (Plan D §C1-0e — keep envelopes transport-specific), so this is the
/// minimal concrete shape Plan D needs for the south tee path.
/// </summary>
public sealed class SouthToolCallInterceptionContext : InterceptionContext;

/// <summary>
/// Response-phase south cartridge middleware. Enriches a successful tool-call outcome with
/// the bound concept's tags (under <c>_ontology.tags</c>) when the result is a JSON object.
/// Non-JSON outputs and errors pass through unchanged.
/// </summary>
/// <remarks>
/// Declares <see cref="InterceptorKind.Mutation"/> because the response-phase JSON gets
/// rewritten. Read-only with respect to upstream state — the enrichment lives entirely on
/// the result payload. Plan C1-11.
/// </remarks>
public sealed class DomainOntologyResponseEnrichmentMiddleware(IDomainOntologyCatalog catalog) : ToolGatewayMiddleware
{
    private readonly IDomainOntologyCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public override InterceptorKind Kind => InterceptorKind.Mutation;

    /// <inheritdoc />
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var outcome = await next().ConfigureAwait(false);
        if (outcome.Error is not null || string.IsNullOrEmpty(outcome.Result)) return outcome;
        if (!_catalog.TryGetConcept(context.ToolName, out var concept)) return outcome;
        if (concept.Tags.Count == 0) return outcome;

        JsonNode? node;
        try { node = JsonNode.Parse(outcome.Result); }
        catch (JsonException) { return outcome; }
        if (node is not JsonObject obj) return outcome;

        var ontologyBlock = new JsonObject();
        var tags = new JsonArray();
        foreach (var tag in concept.Tags) tags.Add(tag);
        ontologyBlock["tags"] = tags;
        ontologyBlock["ontologyVersion"] = _catalog.OntologyVersion;
        obj["_ontology"] = ontologyBlock;

        return outcome with { Result = obj.ToJsonString() };
    }
}
