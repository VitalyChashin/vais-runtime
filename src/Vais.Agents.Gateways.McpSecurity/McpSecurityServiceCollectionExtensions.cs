// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.McpSecurity;

/// <summary>DI extension methods for McpSecurity gateway middleware.</summary>
public static class McpSecurityServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ToolArgumentValidationMiddleware"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolArgumentValidationMiddleware(
        this IServiceCollection services,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredArgsByTool)
    {
        services.AddSingleton<ToolGatewayMiddleware>(
            new ToolArgumentValidationMiddleware(requiredArgsByTool));
        return services;
    }

    /// <summary>Registers <see cref="ToolOutputLengthGuard"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolOutputLengthGuard(
        this IServiceCollection services,
        int maxCharacters)
    {
        services.AddSingleton<ToolGatewayMiddleware>(new ToolOutputLengthGuard(maxCharacters));
        return services;
    }
}
