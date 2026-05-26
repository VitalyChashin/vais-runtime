// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Mcp.Server.Ontology;

/// <summary>
/// Substrate-shaped re-expression of Plan A ND-7 / Plan B NB-12 manifest validation: wraps
/// <see cref="ManifestValidator.ValidateAsync"/> (schema check + cross-ref integrity over
/// <c>IOntologyCatalog</c>) so deployer interceptors can layer additional checks via the
/// same chain. Merges its own findings with any downstream <see cref="ValidationOutcome"/>.
/// </summary>
/// <remarks>
/// Declares <see cref="InterceptorKind.Validation"/> — this interceptor must not mutate any
/// registry. <see cref="ManifestValidator.ValidateAsync"/> is read-only by construction
/// (only <c>DesignRegistryRouter.GetAsync</c> is invoked for cross-ref resolution).
/// <para>
/// Takes <see cref="IServiceProvider"/> to forward to <c>ManifestValidator.ValidateAsync</c>
/// since the legacy validator pulls <c>IOntologyCatalog</c> and the registry handles per call
/// — this matches the existing inline shape for byte parity. A future cleanup may inject
/// the catalog and a typed router directly.
/// </para>
/// </remarks>
internal sealed class ManifestValidatorInterceptor(IServiceProvider services)
    : OntologyInterceptor<DesignValidateInterceptionContext, ValidationOutcome>
{
    private readonly IServiceProvider _services = services;

    public override InterceptorKind Kind => InterceptorKind.Validation;

    public override async Task<ValidationOutcome> InvokeAsync(
        DesignValidateInterceptionContext context,
        Func<Task<ValidationOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var (ok, errors, suggestions) = await ManifestValidator
            .ValidateAsync(context.ManifestJson, _services, cancellationToken)
            .ConfigureAwait(false);

        var downstream = await next().ConfigureAwait(false);
        if (downstream.Errors.Count == 0 && downstream.Suggestions.Count == 0 && downstream.Ok)
            return new ValidationOutcome(ok, errors, suggestions);

        // Merge with deployer-added interceptors (if any) — preserve the legacy
        // single-validator output when nothing downstream contributed.
        var mergedErrors = new List<string>(errors.Count + downstream.Errors.Count);
        mergedErrors.AddRange(errors);
        mergedErrors.AddRange(downstream.Errors);
        var mergedSuggestions = new List<string>(suggestions.Count + downstream.Suggestions.Count);
        mergedSuggestions.AddRange(suggestions);
        mergedSuggestions.AddRange(downstream.Suggestions);
        return new ValidationOutcome(ok && downstream.Ok, mergedErrors, mergedSuggestions);
    }
}
