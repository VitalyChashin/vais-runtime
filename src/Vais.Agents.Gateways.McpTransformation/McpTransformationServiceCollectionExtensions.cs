// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.McpTransformation;

/// <summary>DI extension methods for McpTransformation gateway middleware.</summary>
public static class McpTransformationServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ToolJsonRepairMiddleware"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolJsonRepairMiddleware(
        this IServiceCollection services)
    {
        services.AddSingleton<ToolGatewayMiddleware, ToolJsonRepairMiddleware>();
        return services;
    }

    /// <summary>Registers <see cref="ToolHtmlToMarkdownMiddleware"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolHtmlToMarkdownMiddleware(
        this IServiceCollection services)
    {
        services.AddSingleton<ToolGatewayMiddleware, ToolHtmlToMarkdownMiddleware>();
        return services;
    }
}
