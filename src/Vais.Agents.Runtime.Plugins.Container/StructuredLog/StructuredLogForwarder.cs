// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Container.StructuredLog;

/// <summary>
/// Receives a parsed <see cref="PluginLogRecord"/> and emits it into the runtime's
/// <see cref="ILogger"/> pipeline so the entry reaches docker-logs, ELK/Loki collectors,
/// and any other configured log sinks.
/// </summary>
internal sealed class StructuredLogForwarder(ILogger logger)
{
    private readonly ILogger _logger = logger;

    public void Forward(PluginLogRecord record, string agentId, string source, string? extensionId)
    {
        var level = ParseLevel(record.Severity);
        if (!_logger.IsEnabled(level)) return;

        var fields = record.Fields;

        if (fields is null or { Count: 0 })
        {
            _logger.Log(level,
                "plugin-log source={Source} agent={AgentId}{Extension}: {Message}",
                source,
                agentId,
                extensionId is not null ? $" ext={extensionId}" : string.Empty,
                record.Message);
        }
        else
        {
            // Flatten fields into the structured log template so sinks like Seq/Loki receive them
            // as first-class indexed fields rather than a serialized bag.
            _logger.Log(level,
                "plugin-log source={Source} agent={AgentId}{Extension}: {Message} fields={Fields}",
                source,
                agentId,
                extensionId is not null ? $" ext={extensionId}" : string.Empty,
                record.Message,
                fields);
        }
    }

    internal static LogLevel ParseLevel(string? severity) => severity?.ToUpperInvariant() switch
    {
        "TRACE"    => LogLevel.Trace,
        "DEBUG"    => LogLevel.Debug,
        "INFO"     => LogLevel.Information,
        "WARN"     => LogLevel.Warning,
        "WARNING"  => LogLevel.Warning,
        "ERROR"    => LogLevel.Error,
        "CRITICAL" => LogLevel.Critical,
        "FATAL"    => LogLevel.Critical,
        _          => LogLevel.Information,
    };
}
