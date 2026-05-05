// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core.PowerFx;

/// <summary>Extensions for registering the PowerFx expression evaluator in DI.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="PowerFxGraphExpressionEvaluator"/> as the singleton
    /// <see cref="IGraphExpressionEvaluator"/>. Required for graph manifests that use
    /// inline <c>when: "=..."</c> PowerFx edge predicates.
    /// </summary>
    public static IServiceCollection AddPowerFxExpressionEvaluator(this IServiceCollection services)
        => services.AddSingleton<IGraphExpressionEvaluator, PowerFxGraphExpressionEvaluator>();
}
