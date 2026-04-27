// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
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

    /// <summary>
    /// Registers <see cref="ToolArgumentValidationMiddleware"/> as a named factory under the key
    /// <c>"ToolArgumentValidation"</c>. Reads <c>requiredArgs</c> from
    /// <see cref="GatewayMiddlewareSpec.Params"/> as a string→string-array map.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolArgumentValidation(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolArgumentValidation",
            (spec, _) =>
            {
                var required = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                if (spec.Params is { } p && p.TryGetProperty("requiredArgs", out var map)
                    && map.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in map.EnumerateObject())
                    {
                        var args = prop.Value.ValueKind == JsonValueKind.Array
                            ? prop.Value.EnumerateArray().Select(e => e.GetString()!).ToArray()
                            : [];
                        required[prop.Name] = args;
                    }
                }
                return new ToolArgumentValidationMiddleware(required);
            }));

    /// <summary>
    /// Registers <see cref="ToolOutputLengthGuard"/> as a named factory under the key
    /// <c>"ToolOutputLengthGuard"</c>. Reads <c>maxCharacters</c> from
    /// <see cref="GatewayMiddlewareSpec.Params"/>; defaults to 4096 when absent.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolOutputLengthGuard(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolOutputLengthGuard",
            (spec, _) =>
            {
                var maxChars = spec.Params is { } p
                    && p.TryGetProperty("maxCharacters", out var v) ? v.GetInt32() : 4096;
                return new ToolOutputLengthGuard(maxChars);
            }));
}
