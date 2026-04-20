// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// Configuration for the OPA policy engine. Bound through
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
/// via <c>AddOpaPolicyEngine(Action&lt;OpaPolicyEngineOptions&gt;)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Defaults target a sidecar-pod deployment: OPA listening on
/// <c>http://opa:8181</c> with a rule at <c>vais/agents/allow</c>.
/// Adapter POSTs <c>{"input": {...}}</c> to
/// <c>{BaseUrl}/v1/data/{DataPath}</c> and parses the response.
/// </para>
/// </remarks>
public sealed class OpaPolicyEngineOptions
{
    /// <summary>
    /// Base URL of the OPA REST server. Defaults to
    /// <c>http://opa:8181</c> — the canonical sidecar-pod convention.
    /// Set to the pod-local OPA instance, a shared service, or a
    /// localhost loopback in single-process dev.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("http://opa:8181");

    /// <summary>
    /// Rule path appended to <c>/v1/data/</c> when querying OPA.
    /// Defaults to <c>vais/agents/allow</c>; consumers with a different
    /// package naming override. Maps to a Rego file declaring
    /// <c>package vais.agents</c> with an <c>allow</c> rule.
    /// </summary>
    public string DataPath { get; set; } = "vais/agents/allow";

    /// <summary>
    /// Per-evaluation HTTP timeout. Exceeded → <see cref="FailMode"/>
    /// kicks in. Loopback OPA is typically 1-5ms; the 500ms default
    /// tolerates cross-pod latency plus ~100x slack.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How the adapter responds when OPA is unreachable / times out /
    /// returns malformed. Defaults to <see cref="OpaFailMode.Closed"/>
    /// (deny) — enterprise-safe.
    /// </summary>
    public OpaFailMode FailMode { get; set; } = OpaFailMode.Closed;

    /// <summary>
    /// TTL for decision caching keyed by SHA-256 of the input payload.
    /// Default 5 seconds. Set to <see cref="TimeSpan.Zero"/> to disable
    /// caching. Stable policy + stable input → cheap cache hit.
    /// </summary>
    public TimeSpan DecisionCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on cached decision entries. Overflow triggers a
    /// 25%-by-oldest-timestamp purge. Default 1024 balances
    /// multi-tenant (many keys) against single-tenant (few keys).
    /// </summary>
    public int DecisionCacheMaxEntries { get; set; } = 1024;

    /// <summary>
    /// When <c>true</c> the adapter lazily queries
    /// <c>GET /v1/status</c> on its first evaluation and logs the
    /// bundle revisions for observability. Non-blocking; failures are
    /// log-debug only.
    /// </summary>
    public bool LogPolicyVersionOnStartup { get; set; } = true;
}
