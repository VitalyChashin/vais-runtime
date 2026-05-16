// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Eval.Assertions;

namespace Vais.Agents.Eval;

/// <summary>DI wiring for the eval assertion pipeline.</summary>
public static class EvalServiceCollectionExtensions
{
    /// <summary>
    /// Registers the assertion factory registry, built-in assertion factories,
    /// and the logging-only result store (replace with a Postgres store for
    /// multi-silo durability).
    /// </summary>
    public static IServiceCollection AddVaisAgentsEval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IEvalAssertionFactory, NoTurnFailedAssertionFactory>();
        services.AddSingleton<IEvalAssertionFactory, ResponseRegexAssertionFactory>();
        services.AddSingleton<IEvalAssertionFactory, ToolCallSequenceAssertionFactory>();
        services.AddSingleton<IEvalAssertionFactory, JudgeScoreAssertionFactory>();

        services.AddSingleton<IEvalAssertionFactoryRegistry, EvalAssertionFactoryRegistry>();
        services.AddSingleton<IEvalResultStore, LoggingEvalResultStore>();

        return services;
    }
}
