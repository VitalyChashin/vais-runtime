// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// SEP-1763 type discriminator. Declares what kind of side-effect an interceptor performs;
/// used for pipeline introspection, conformance assertions, and tooling. The pipeline
/// executor itself does not branch on this — the discriminator is metadata, not control flow.
/// </summary>
public enum InterceptorKind
{
    /// <summary>Inspects state and short-circuits with a denial / suggestion. Must not mutate payloads on the pass-through path.</summary>
    Validation = 0,

    /// <summary>Rewrites the request, the response, or both. Carries observable effect on downstream consumers.</summary>
    Mutation = 1,

    /// <summary>Observes without altering. Default. Includes telemetry, trajectory tees, audit logs.</summary>
    Observability = 2,
}

/// <summary>
/// SEP-1763 phase discriminator. Declares which phase of the interception lifecycle the
/// interceptor cares about. Declarative metadata only — the pipeline still calls
/// <c>InvokeAsync</c> uniformly; the value enables tooling and conformance checks.
/// </summary>
[Flags]
public enum InterceptorPhase
{
    /// <summary>Runs before <c>await next()</c>.</summary>
    Request = 1,

    /// <summary>Runs after <c>await next()</c>.</summary>
    Response = 2,

    /// <summary>Runs in both phases. Default.</summary>
    Both = Request | Response,
}

/// <summary>
/// Non-generic base for the ontology-interceptor substrate (SEP-1763). Carries the
/// cross-cutting metadata (<see cref="Kind"/>, <see cref="Phase"/>) that lets the system
/// reason about an interceptor without knowing which transport it adapts.
/// </summary>
/// <remarks>
/// This base is *only* metadata. The actual interception lifecycle is defined by the
/// transport-typed subclasses — <see cref="OntologyInterceptor{TContext, TOutcome}"/> for
/// new interceptors written against the substrate, and the legacy
/// <see cref="ToolGatewayMiddleware"/> for the existing south tool dispatch chain (re-based
/// here as a P6 adapter — see Plan C1 §3 Decisions).
/// <para>
/// Instances must be reentrant — do not store per-call state on instance fields.
/// </para>
/// </remarks>
public abstract class OntologyInterceptor
{
    /// <summary>SEP-1763 type discriminator. Defaults to <see cref="InterceptorKind.Observability"/> (least-privileged default).</summary>
    public virtual InterceptorKind Kind => InterceptorKind.Observability;

    /// <summary>SEP-1763 phase discriminator. Defaults to <see cref="InterceptorPhase.Both"/>.</summary>
    public virtual InterceptorPhase Phase => InterceptorPhase.Both;
}

/// <summary>
/// Transport-typed base for ontology interceptors. Subclass with a concrete
/// <typeparamref name="TContext"/> (an <see cref="InterceptionContext"/> derivative) and a
/// transport-specific outcome type to participate in a pipeline composed by
/// <see cref="OntologyInterceptorChain.Compose{TContext, TOutcome}"/>.
/// </summary>
/// <typeparam name="TContext">Concrete interception context type carried through the chain.</typeparam>
/// <typeparam name="TOutcome">Outcome type the chain produces (e.g. a tool-call result, a list-tools envelope).</typeparam>
/// <remarks>
/// Short-circuit by returning a synthetic outcome without calling <c>next</c>. Pass-through is
/// the default; override <see cref="InvokeAsync"/> only to do real work.
/// </remarks>
public abstract class OntologyInterceptor<TContext, TOutcome> : OntologyInterceptor
    where TContext : InterceptionContext
{
    /// <summary>
    /// Intercept one trip through the chain. Call <paramref name="next"/> to pass through;
    /// short-circuit by returning a synthetic outcome without calling <paramref name="next"/>.
    /// </summary>
    public virtual Task<TOutcome> InvokeAsync(
        TContext context,
        Func<Task<TOutcome>> next,
        CancellationToken cancellationToken = default)
        => next();
}

/// <summary>
/// Composes an <see cref="OntologyInterceptor{TContext, TOutcome}"/> sequence into a single
/// callable chain. Registration order is outer-to-inner: index 0 wraps everything; the
/// terminal delegate runs innermost.
/// </summary>
public static class OntologyInterceptorChain
{
    /// <summary>
    /// Build the composed pipeline. The returned delegate, when awaited, runs the
    /// interceptors in registration order and finally invokes <paramref name="terminal"/>.
    /// Pass the same <paramref name="context"/> instance through every interceptor.
    /// </summary>
    public static Func<Task<TOutcome>> Compose<TContext, TOutcome>(
        IReadOnlyList<OntologyInterceptor<TContext, TOutcome>> interceptors,
        TContext context,
        Func<Task<TOutcome>> terminal,
        CancellationToken cancellationToken = default)
        where TContext : InterceptionContext
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terminal);

        var chain = terminal;
        for (var i = interceptors.Count - 1; i >= 0; i--)
        {
            var mw = interceptors[i];
            var prev = chain;
            chain = () => mw.InvokeAsync(context, prev, cancellationToken);
        }
        return chain;
    }
}
