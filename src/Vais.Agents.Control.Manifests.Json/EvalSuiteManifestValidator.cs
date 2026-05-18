// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Phase-E1 stub that accepts any registered assertion kind. Replaced by a real
/// implementation when the kind registry is populated in Phase E2.
/// </summary>
internal sealed class EmptyEvalAssertionKindRegistry : IEvalAssertionKindRegistry
{
    public bool IsRegistered(string kind) => true;
}

/// <summary>
/// Post-parse semantic validation for <see cref="EvalSuiteManifest"/>.
/// Called after <see cref="EvalSuiteManifestParser"/> returns a non-null manifest.
/// Collects validation errors as URN strings into the supplied list.
/// </summary>
internal static class EvalSuiteManifestSemanticValidator
{
    internal static void Validate(
        EvalSuiteManifest manifest,
        IEvalAssertionKindRegistry kindRegistry,
        List<string> errors)
    {
        var spec = manifest.Spec;
        var hasCases = spec.Cases is { Count: > 0 };
        var hasSampling = spec.Sampling is not null;

        // Mode discriminator: exactly one of cases or sampling must be set
        if (hasCases && hasSampling)
            errors.Add("urn:vais-agents:eval-suite-mode-conflict");
        else if (!hasCases && !hasSampling)
            errors.Add("urn:vais-agents:eval-suite-mode-missing");

        if (hasSampling)
        {
            var sampling = spec.Sampling!;

            // Assertions required in continuous mode
            if (spec.Assertions is not { Count: > 0 })
                errors.Add("urn:vais-agents:eval-suite-sampling-assertions-required");

            // Baseline not allowed in continuous mode
            if (spec.Baseline is not null)
                errors.Add("urn:vais-agents:eval-suite-sampling-baseline-conflict");

            // Cached replay not allowed in continuous mode
            if (spec.ReplayMode == EvalReplayMode.Cached)
                errors.Add("urn:vais-agents:eval-suite-sampling-cached-replay");

            // Rate must be in (0, 1]
            if (sampling.Rate <= 0 || sampling.Rate > 1)
                errors.Add("urn:vais-agents:eval-suite-sampling-rate-range");

            // WindowDuration must be in [1 min, 24 h]
            if (sampling.WindowDuration < TimeSpan.FromMinutes(1) || sampling.WindowDuration > TimeSpan.FromHours(24))
                errors.Add("urn:vais-agents:eval-suite-sampling-window-range");

            // Validate spec-level assertion kinds
            if (spec.Assertions is not null)
            {
                for (var i = 0; i < spec.Assertions.Count; i++)
                {
                    var kind = spec.Assertions[i].Kind;
                    if (!kindRegistry.IsRegistered(kind))
                        errors.Add($"urn:vais-agents:eval-suite-unknown-assertion-kind:{kind}");
                }
            }
        }
    }
}
