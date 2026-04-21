// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// In-process <see cref="IAgentLifecycleManager"/> engine. Routes the seven
/// universal verbs through a policy + audit middleware layer; holds manifest
/// state in a supplied <see cref="IAgentRegistry"/>; delegates runtime agent
/// access to a supplied <see cref="IAgentRuntime"/>. Runtime-neutral: works
/// identically over <c>InMemoryAgentRuntime</c> and <c>OrleansAgentRuntime</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.6 scope.</b> The manager wires policy + audit around the verbs and tracks
/// cancel / status state in-process. It does <em>not</em> materialize runtime
/// agents from <see cref="AgentManifest.Model"/> + <see cref="AgentManifest.SystemPrompt"/>
/// today — the consumer's DI is expected to register concrete
/// <see cref="IAiAgent"/> instances with the supplied <see cref="IAgentRuntime"/>
/// under the manifest's <see cref="AgentManifest.Id"/> key. Manifest-to-runtime
/// materialization (model resolution, prompt composition, tool registry
/// assembly) ships with a later pillar; the shape here leaves the seam wide open.
/// </para>
/// <para>
/// <b>Signal in v0.6.</b> <see cref="SignalAsync"/> is a policy-gated, audited
/// no-op — the in-process engine has nowhere to route a signal until the
/// durable-execution journal's pause state wires into the grain / session layer
/// (post-v0.6 follow-up). The verb audits + returns so callers can exercise the
/// full lifecycle surface against the in-process engine without runtime errors.
/// </para>
/// </remarks>
public sealed class AgentLifecycleManager : IAgentLifecycleManager
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentRuntime _runtime;
    private readonly IAgentPolicyEngine _policy;
    private readonly IAuditLog _audit;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly IAgentManifestInvalidator? _invalidator;
    private readonly ILogger<AgentLifecycleManager> _logger;
    private readonly ConcurrentDictionary<(string Id, string Version), AgentState> _state = new();

    /// <summary>Shared error code consumers can match on to translate to HTTP 404 etc.</summary>
    public const string UnknownAgentErrorType = "AgentHandleNotFound";

    /// <summary>Construct a manager. Registry + runtime are required; policy + audit + accessor + invalidator fall back to null defaults.</summary>
    public AgentLifecycleManager(
        IAgentRegistry registry,
        IAgentRuntime runtime,
        IAgentPolicyEngine? policy = null,
        IAuditLog? audit = null,
        IAgentContextAccessor? contextAccessor = null,
        ILogger<AgentLifecycleManager>? logger = null,
        IAgentManifestInvalidator? invalidator = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runtime);
        _registry = registry;
        _runtime = runtime;
        _policy = policy ?? NullAgentPolicyEngine.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _contextAccessor = contextAccessor ?? new AsyncLocalAgentContextAccessorFallback();
        _invalidator = invalidator;
        _logger = logger ?? NullLogger<AgentLifecycleManager>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Create, manifest, principal, cancellationToken).ConfigureAwait(false);

        using var activity = StartVerbActivity(PolicyOperation.Create, manifest, principal);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? errorType = null;
        try
        {
            // Simplest v0.6 registry contract — the built-in InMemoryAgentRegistry exposes a
            // non-interface Register helper; other registries may not. Fall back to a generic
            // reflection-free "call Register if it's there" pattern via duck-typing.
            RegisterManifest(manifest);
            var handle = new AgentHandle(manifest.Id, manifest.Version, InstanceId: null);
            _state[(manifest.Id, manifest.Version)] = new AgentState();
            return handle;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
            await AuditAsync(PolicyOperation.Create, manifest.Id, manifest.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
            RecordVerb(PolicyOperation.Create, outcome: errorType is null ? "allowed" : "errored", duration: sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var manifest = await ResolveManifestAsync(handle, cancellationToken).ConfigureAwait(false);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Invoke, manifest, principal, cancellationToken).ConfigureAwait(false);

        if (!_state.TryGetValue((handle.AgentId, handle.Version), out var state))
        {
            await AuditAsync(PolicyOperation.Invoke, handle.AgentId, handle.Version, principal, allowed: true, denyReason: null, errorType: UnknownAgentErrorType).ConfigureAwait(false);
            RecordVerb(PolicyOperation.Invoke, outcome: "errored", duration: null);
            throw new InvalidOperationException($"Unknown agent handle: {handle.AgentId}/{handle.Version}. Call CreateAsync first.");
        }

        using var activity = StartVerbActivity(PolicyOperation.Invoke, manifest, principal);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.Register(cts);
        string? errorType = null;
        try
        {
            var agent = _runtime.GetOrCreate(handle.AgentId);
            var reply = await agent.AskAsync(request.Text, cts.Token).ConfigureAwait(false);
            return new AgentInvocationResult(
                Text: reply,
                SessionId: request.SessionId,
                Metadata: request.Metadata);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errorType = ex.GetType().Name;
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
            state.Unregister(cts);
            await AuditAsync(PolicyOperation.Invoke, handle.AgentId, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
            RecordVerb(PolicyOperation.Invoke, outcome: errorType is null ? "allowed" : "errored", duration: sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(signal);

        var manifest = await ResolveManifestAsync(handle, cancellationToken).ConfigureAwait(false);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Signal, manifest, principal, cancellationToken).ConfigureAwait(false);

        // v0.6 signal routing is contract-only for the in-process engine — see class-level remarks.
        _logger.LogDebug("Signal {Kind} received for {AgentId}/{Version}; no-op in v0.6 in-process engine.", signal.Kind, handle.AgentId, handle.Version);
        await AuditAsync(PolicyOperation.Signal, handle.AgentId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // Query may hit an unknown handle — manifest lookup is best-effort, not a throw.
        var manifest = await _registry.GetAsync(handle.AgentId, handle.Version, cancellationToken).ConfigureAwait(false);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Query, manifest, principal, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_state.TryGetValue((handle.AgentId, handle.Version), out var state))
            {
                return AgentStatus.Unknown;
            }
            return state.IsRunning ? AgentStatus.Active : AgentStatus.Idle;
        }
        finally
        {
            await AuditAsync(PolicyOperation.Query, handle.AgentId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var manifest = await ResolveManifestAsync(handle, cancellationToken).ConfigureAwait(false);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Cancel, manifest, principal, cancellationToken).ConfigureAwait(false);

        try
        {
            if (_state.TryGetValue((handle.AgentId, handle.Version), out var state))
            {
                state.CancelAll();
            }
        }
        finally
        {
            await AuditAsync(PolicyOperation.Cancel, handle.AgentId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(newManifest);

        if (!string.Equals(handle.AgentId, newManifest.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Handle agent id '{handle.AgentId}' does not match new manifest id '{newManifest.Id}'.",
                nameof(newManifest));
        }

        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Update, newManifest, principal, cancellationToken).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            RegisterManifest(newManifest);
            _state[(newManifest.Id, newManifest.Version)] = new AgentState();

            // v0.17 Pillar B eviction: drop the runtime's cached IAiAgent + invalidate
            // the translator cache so the NEXT invoke re-activates the grain with the
            // updated manifest. In-flight runs keep the manifest they started with.
            _runtime.Remove(newManifest.Id);
            if (_invalidator is not null)
            {
                await _invalidator.InvalidateAsync(newManifest.Id, cancellationToken).ConfigureAwait(false);
            }

            return new AgentHandle(newManifest.Id, newManifest.Version, InstanceId: null);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.Update, newManifest.Id, newManifest.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var manifest = await _registry.GetAsync(handle.AgentId, handle.Version, cancellationToken).ConfigureAwait(false);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.Evict, manifest, principal, cancellationToken).ConfigureAwait(false);

        try
        {
            if (_state.TryRemove((handle.AgentId, handle.Version), out var state))
            {
                state.CancelAll();
            }
            RemoveManifest(handle.AgentId, handle.Version);
            _runtime.Remove(handle.AgentId);

            // v0.17 Pillar B: clear the translator's options cache for this id
            // so no stale StatefulAgentOptions lingers if the id is reused.
            if (_invalidator is not null)
            {
                await _invalidator.InvalidateAsync(handle.AgentId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await AuditAsync(PolicyOperation.Evict, handle.AgentId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    private async ValueTask<AgentManifest?> ResolveManifestAsync(AgentHandle handle, CancellationToken ct)
    {
        var manifest = await _registry.GetAsync(handle.AgentId, handle.Version, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            // Still return null — let the policy engine see the null manifest + decide; the
            // verb impl throws the UnknownAgent error with the audit entry already in flight.
        }
        return manifest;
    }

    private async ValueTask GateAsync(PolicyOperation op, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken ct)
    {
        var decision = await _policy.EvaluateAsync(op, manifest, principal, ct).ConfigureAwait(false);
        if (decision.IsAllowed)
        {
            return;
        }
        var reason = decision.Reason ?? "policy denied";
        await AuditAsync(op, manifest?.Id, manifest?.Version, principal, allowed: false, denyReason: reason, errorType: null).ConfigureAwait(false);
        RecordVerb(op, outcome: "denied", duration: null);
        throw new AgentPolicyDeniedException(op, reason);
    }

    private static System.Diagnostics.Activity? StartVerbActivity(PolicyOperation op, AgentManifest? manifest, AgentPrincipal? principal)
    {
        var activity = ControlPlaneDiagnostics.ActivitySource.StartActivity($"control.{op.ToString().ToLowerInvariant()}", System.Diagnostics.ActivityKind.Server);
        if (activity is null) return null;
        activity.SetTag("vais.control.verb", op.ToString());
        if (manifest is not null)
        {
            activity.SetTag("vais.agent.id", manifest.Id);
            activity.SetTag("vais.agent.version", manifest.Version);
        }
        if (principal is not null)
        {
            activity.SetTag("vais.principal.id", principal.Id);
            if (principal.TenantId is not null) activity.SetTag("vais.principal.tenant", principal.TenantId);
        }
        return activity;
    }

    private static void RecordVerb(PolicyOperation op, string outcome, double? duration)
    {
        var tags = new System.Collections.Generic.KeyValuePair<string, object?>[]
        {
            new("vais.control.verb", op.ToString()),
            new("vais.control.outcome", outcome),
        };
        ControlPlaneDiagnostics.VerbCount.Add(1, tags);
        if (duration is double d)
        {
            ControlPlaneDiagnostics.VerbDuration.Record(d, tags);
        }
    }

    private async ValueTask AuditAsync(
        PolicyOperation op,
        string? agentId,
        string? version,
        AgentPrincipal? principal,
        bool allowed,
        string? denyReason,
        string? errorType)
    {
        var entry = new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: op,
            AgentId: agentId,
            AgentVersion: version,
            PrincipalId: principal?.Id ?? "anonymous",
            TenantId: principal?.TenantId,
            Allowed: allowed,
            DenyReason: denyReason,
            ErrorType: errorType);
        try
        {
            await _audit.AppendAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit-write failures must not break the verb — log and move on.
            _logger.LogWarning(ex, "Audit log {AuditType} threw; swallowed.", _audit.GetType().Name);
        }
    }

    private AgentPrincipal? SynthesizePrincipal()
    {
        var ctx = _contextAccessor.Current;
        if (ctx.UserId is { Length: > 0 } userId)
        {
            return new AgentPrincipal(userId, ctx.TenantId, Scopes: null);
        }
        return null;
    }

    private void RegisterManifest(AgentManifest manifest)
    {
        // Duck-type onto a concrete Register method when the registry exposes one
        // (the built-in InMemoryAgentRegistry does). Other registries can implement
        // their own manager or subclass this one and override RegisterManifest.
        var method = _registry.GetType().GetMethod("Register", new[] { typeof(AgentManifest) });
        if (method is null)
        {
            throw new NotSupportedException(
                $"{_registry.GetType().Name} does not expose a public Register(AgentManifest) method. " +
                "Subclass AgentLifecycleManager and override RegisterManifest, or supply an IAgentRegistry that does.");
        }
        method.Invoke(_registry, new object[] { manifest });
    }

    private void RemoveManifest(string agentId, string version)
    {
        var method = _registry.GetType().GetMethod("Remove", new[] { typeof(string), typeof(string) });
        method?.Invoke(_registry, new object[] { agentId, version });
    }

    private sealed class AgentState
    {
        private readonly HashSet<CancellationTokenSource> _inFlight = new();
        private readonly object _gate = new();

        public bool IsRunning
        {
            get { lock (_gate) { return _inFlight.Count > 0; } }
        }

        public void Register(CancellationTokenSource cts)
        {
            lock (_gate) { _inFlight.Add(cts); }
        }

        public void Unregister(CancellationTokenSource cts)
        {
            lock (_gate) { _inFlight.Remove(cts); }
        }

        public void CancelAll()
        {
            CancellationTokenSource[] snapshot;
            lock (_gate) { snapshot = _inFlight.ToArray(); }
            foreach (var cts in snapshot)
            {
                try { cts.Cancel(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Minimal <see cref="IAgentContextAccessor"/> fallback when the consumer doesn't
    /// wire one. Returns <see cref="AgentContext.Empty"/>; used only for principal
    /// synthesis in the lifecycle manager.
    /// </summary>
    private sealed class AsyncLocalAgentContextAccessorFallback : IAgentContextAccessor
    {
        public AgentContext Current => AgentContext.Empty;
    }
}
