// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.AgentLogs;

/// <summary>Extension methods for registering the agent log sink.</summary>
public static class AgentLogSinkExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IAgentLogSink"/> and an <see cref="ILoggerProvider"/>
    /// that captures log lines from Orleans agent grains into the sink.
    /// </summary>
    public static IServiceCollection AddAgentLogSink(
        this IServiceCollection services,
        Action<AgentLogSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IAgentLogSink>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentLogSinkOptions>>().Value;
            return new InMemoryAgentLogSink(opts.BufferLinesPerAgent);
        });

        services.AddSingleton<ILoggerProvider>(sp =>
            new AgentGrainLoggerProvider(sp.GetRequiredService<IAgentLogSink>()));

        return services;
    }
}
