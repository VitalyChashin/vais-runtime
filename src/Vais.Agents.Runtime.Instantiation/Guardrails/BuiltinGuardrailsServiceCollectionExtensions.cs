// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Runtime.Instantiation.Guardrails;

/// <summary>
/// DI entry point for the v0.17 built-in guardrail factories (LengthCap,
/// RegexAllowlist × {Input, Output}, RegexDenylist × {Input, Output},
/// LlmAsJudge).
/// </summary>
public static class BuiltinGuardrailsServiceCollectionExtensions
{
    /// <summary>
    /// Register all six built-in guardrail factories. Consumers add their own
    /// via <c>services.AddSingleton&lt;IGuardrailFactory, MyCustomFactory&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddBuiltinGuardrails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IGuardrailFactory, LengthCapInputGuardrailFactory>();
        services.AddSingleton<IGuardrailFactory, RegexAllowlistInputGuardrailFactory>();
        services.AddSingleton<IGuardrailFactory, RegexAllowlistOutputGuardrailFactory>();
        services.AddSingleton<IGuardrailFactory, RegexDenylistInputGuardrailFactory>();
        services.AddSingleton<IGuardrailFactory, RegexDenylistOutputGuardrailFactory>();
        services.AddSingleton<IGuardrailFactory, LlmAsJudgeOutputGuardrailFactory>();

        return services;
    }
}
