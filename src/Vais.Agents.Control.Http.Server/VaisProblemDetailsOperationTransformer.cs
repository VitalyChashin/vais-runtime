// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vais.Agents.Control.Http;

/// <summary>
/// OpenAPI operation transformer that annotates each Problem Details response
/// (4xx/5xx) with an <c>x-vais-type-urns</c> extension listing the stable
/// <c>urn:vais-agents:*</c> type URNs the server may return for that status.
/// Consumers running client codegen can pattern-match on these URNs at runtime
/// without resorting to string parsing of the generic Problem Details schema.
/// </summary>
/// <remarks>
/// Registered automatically by
/// <see cref="AgentControlPlaneOpenApiServiceCollectionExtensions.AddAgentControlPlaneOpenApi"/>.
/// Status-to-URN map kept in one place here so evolution (new URN types, URN
/// renames) touches a single file.
/// </remarks>
public sealed class VaisProblemDetailsOperationTransformer : IOpenApiOperationTransformer
{
    private static readonly IReadOnlyDictionary<string, string[]> _urnsByStatus = new Dictionary<string, string[]>
    {
        ["400"] = new[]
        {
            ProblemDetailsMapping.ManifestInvalidType,
            ProblemDetailsMapping.TypePrefix + "bad-request",
        },
        ["403"] = new[] { ProblemDetailsMapping.PolicyDeniedType },
        ["404"] = new[] { ProblemDetailsMapping.AgentNotFoundType },
        ["409"] = new[]
        {
            ProblemDetailsMapping.InterruptPendingType,
            ProblemDetailsMapping.IdempotencyInFlightType,
        },
        ["422"] = new[] { ProblemDetailsMapping.IdempotencyMismatchType },
        ["429"] = new[] { ProblemDetailsMapping.BudgetExceededType },
        ["501"] = new[] { ProblemDetailsMapping.StreamingNotSupportedType },
        ["503"] = new[] { ProblemDetailsMapping.BackendUnavailableType },
    };

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.Responses is null)
        {
            return Task.CompletedTask;
        }

        foreach (var (statusCode, response) in operation.Responses)
        {
            if (_urnsByStatus.TryGetValue(statusCode, out var urns) && response is OpenApiResponse concrete)
            {
                var array = new JsonArray();
                foreach (var urn in urns)
                {
                    array.Add(urn);
                }
                concrete.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                concrete.Extensions["x-vais-type-urns"] = new JsonNodeExtension(array);
            }
        }

        return Task.CompletedTask;
    }
}
