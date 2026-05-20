// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Vais.Agents.Runtime.Plugins.Container.StructuredLog;

/// <summary>
/// Wire shape for a single structured log record posted to <c>POST /v1/logs</c>.
/// </summary>
internal sealed class PluginLogRecord
{
    /// <summary>ISO-8601 timestamp. Defaults to request-receipt time when absent or unparseable.</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    /// <summary>Severity label: TRACE, DEBUG, INFO, WARN, ERROR, CRITICAL. Case-insensitive.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    /// <summary>Human-readable message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Arbitrary structured fields emitted by the plugin. Values serialized
    /// to string when forwarded to the runtime <see cref="Microsoft.Extensions.Logging.ILogger"/>.
    /// </summary>
    [JsonPropertyName("fields")]
    public Dictionary<string, object?>? Fields { get; init; }
}
