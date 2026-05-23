// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>Options for <see cref="ContainerPluginServiceCollectionExtensions.AddContainerPlugins"/>.</summary>
public sealed class ContainerPluginLoaderOptions
{
    /// <summary>Root directory scanned for plugin subfolders containing <c>plugin.yaml</c>.</summary>
    public string PluginsDirectory { get; set; } = "plugins";

    /// <summary>Base URL of the internal gateway (e.g. <c>http://localhost:5001</c>).</summary>
    public string InternalGatewayBaseUrl { get; set; } = "http://localhost:5001";

    /// <summary>Minimum supported container API version (inclusive).</summary>
    public string SupportedApiVersionMin { get; set; } = "0.24";

    /// <summary>Maximum supported container API version (inclusive). 0.25 adds the 502/503/504 error
    /// codes; 0.26 adds the optional session-mode <c>context.renewTokenUrl</c> field — both additive
    /// over 0.24, and 0.24 plugins remain accepted.</summary>
    public string SupportedApiVersionMax { get; set; } = "0.26";

    /// <summary>Operator-configured upper bounds for per-plugin resource requests.</summary>
    public ContainerPluginResourceBounds ResourceBounds { get; set; } = new();

    /// <summary>
    /// Lifetime in seconds of the short, renewable call tokens issued in session mode
    /// (when a plugin sets <c>spec.sessionTtlSeconds</c>). The plugin SDK refreshes the token via
    /// the renewal endpoint before it expires. Smaller = smaller blast radius for a leaked token,
    /// at the cost of more frequent renewals. Ignored by short-turn plugins (single full-TTL token).
    /// Set via <c>VAIS_CONTAINER_PLUGIN_RENEW_TTL_SECONDS</c>. Default 120.
    /// </summary>
    public int RenewTokenTtlSeconds { get; set; } = 120;

    /// <summary>
    /// Docker network name for internal-network mode (Phase 2 egress isolation).
    /// When set, plugin containers are attached to this network and addressed via container-DNS;
    /// no host port is published. Null or empty = legacy host-runtime mode.
    /// Set via <c>VAIS_DOCKER_PLUGIN_NETWORK</c> (e.g. <c>vais-internal</c>).
    /// </summary>
    public string? PluginNetwork { get; set; }

    /// <summary>
    /// OTLP HTTP endpoint injected into Docker plugin containers as
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>. When set, the runtime also injects
    /// <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf</c> and a per-plugin HMAC token
    /// in the <c>OTEL_EXPORTER_OTLP_HEADERS</c> variable so spans arrive authenticated.
    /// Defaults to the runtime's own internal gateway URL (<c>/v1/otlp</c>).
    /// Set to null to disable OTLP injection.
    /// </summary>
    public string? OtlpEndpointUrl { get; set; }

    /// <summary>
    /// Base URL of the structured-log endpoint injected into Docker plugin containers as
    /// <c>VAIS_LOG_ENDPOINT</c> (with <c>?source=plugin&amp;id=&lt;plugin-name&gt;</c> appended).
    /// The runtime also injects <c>VAIS_LOG_TOKEN</c> with a 24-hour HMAC token so log records
    /// arrive authenticated. Set to null to disable structured-log injection.
    /// </summary>
    public string? LogEndpointUrl { get; set; }
}

/// <summary>
/// Upper bounds applied to resource requests in <c>spec.resources</c> of a plugin manifest.
/// Operators can raise or lower these to match cluster capacity.
/// </summary>
public sealed class ContainerPluginResourceBounds
{
    /// <summary>Maximum memory a plugin may request. Default 2 GiB.</summary>
    public long MaxMemoryBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum CPU a plugin may request, in nanoCPUs (1 CPU = 1_000_000_000). Default 4 vCPU.</summary>
    public long MaxNanoCpus    { get; init; } = 4_000_000_000L;

    /// <summary>Maximum PID limit a plugin may request. Default 1024.</summary>
    public long MaxPidsLimit   { get; init; } = 1024;
}
