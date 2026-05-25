// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Control.Http;

/// <summary>
/// DI + endpoint-builder extensions that register a .NET 10
/// <c>Microsoft.AspNetCore.OpenApi</c> document describing the control-plane
/// REST surface. Zero new package references — the API ships with
/// <c>Microsoft.AspNetCore.App</c> and is reachable via the existing
/// <c>FrameworkReference</c>.
/// </summary>
public static class AgentControlPlaneOpenApiServiceCollectionExtensions
{
    /// <summary>Default document name — matches the <c>/v1</c> URL prefix.</summary>
    public const string DefaultDocumentName = "v1";

    /// <summary>
    /// Register the OpenAPI document generator + the
    /// <see cref="VaisProblemDetailsOperationTransformer"/> that enriches error
    /// responses with <c>x-vais-type-urns</c> metadata. Consumers call this once
    /// alongside <see cref="AgentControlPlaneServiceCollectionExtensions.AddAgentControlPlane"/>.
    /// </summary>
    public static IServiceCollection AddAgentControlPlaneOpenApi(
        this IServiceCollection services,
        string documentName = DefaultDocumentName,
        Action<OpenApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);

        services.AddOpenApi(documentName, options =>
        {
            options.AddOperationTransformer<VaisProblemDetailsOperationTransformer>();
            configure?.Invoke(options);
        });
        return services;
    }

    /// <summary>
    /// Mount the OpenAPI document endpoint. Default pattern
    /// <c>/openapi/{documentName}.json</c>; override to expose a different URL
    /// (e.g., <c>/spec/{documentName}.json</c>).
    /// </summary>
    public static IEndpointRouteBuilder MapAgentControlPlaneOpenApi(
        this IEndpointRouteBuilder builder,
        string pattern = "/openapi/{documentName}.json")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        builder.MapOpenApi(pattern);
        return builder;
    }
}
