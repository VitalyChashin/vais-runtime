// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Singleton <see cref="IManifestApplyDiagnosticsSink"/> implementation for the HTTP
/// control plane. Uses <see cref="AsyncLocal{T}"/> to isolate warnings per logical
/// request flow so concurrent applies don't bleed into one another.
/// </summary>
/// <remarks>
/// Registered by <see cref="AgentControlPlaneServiceCollectionExtensions.AddAgentControlPlane"/>
/// as both the concrete type (for HTTP handlers that call <see cref="BeginCapture"/>) and
/// the <see cref="IManifestApplyDiagnosticsSink"/> interface (for the manifest translator).
/// Because it uses <c>AddSingleton</c> (not <c>TryAddSingleton</c>) for the interface
/// binding, the HTTP layer always wins — hosts that want to fan-out to a custom sink
/// must wrap this instance rather than replacing the interface registration.
/// </remarks>
internal sealed class CapturingManifestApplyDiagnosticsSink : IManifestApplyDiagnosticsSink
{
    private static readonly AsyncLocal<List<ApplyDiagnostic>?> _scope = new();

    public void Record(string agentId, string urn, string detail)
        => _scope.Value?.Add(new ApplyDiagnostic(urn, detail));

    /// <summary>
    /// Open a capture scope for the current async flow. The returned scope must be
    /// disposed (e.g. via <c>using</c>) so the <see cref="AsyncLocal{T}"/> slot is
    /// cleared even when the handler exits via an exception.
    /// </summary>
    public CaptureScope BeginCapture()
    {
        _scope.Value = [];
        return new CaptureScope();
    }

    internal sealed class CaptureScope : IDisposable
    {
        private bool _drained;

        /// <summary>Return collected warnings and clear the scope.</summary>
        public IReadOnlyList<ApplyDiagnostic> Drain()
        {
            var result = (_scope.Value ?? []).ToArray();
            _scope.Value = null;
            _drained = true;
            return result;
        }

        public void Dispose()
        {
            if (!_drained)
                _scope.Value = null;
        }
    }
}
