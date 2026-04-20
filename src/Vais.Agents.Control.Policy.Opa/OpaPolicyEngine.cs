// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// <see cref="IAgentPolicyEngine"/> implementation backed by an
/// Open Policy Agent sidecar reached over HTTP. Every lifecycle verb
/// the control plane dispatches routes through
/// <see cref="EvaluateAsync"/> → OPA's data API → a
/// <see cref="PolicyDecision"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wire shape: <c>POST {BaseUrl}/v1/data/{DataPath}</c> with body
/// <c>{"input": {schemaVersion, operation, principal, agent}}</c>.
/// Response body is parsed by <see cref="OpaResponseParser"/> which
/// accepts both <c>{"result": true|false}</c> and
/// <c>{"result": {"allowed": true|false, "reason": "..."}}</c> shapes.
/// </para>
/// <para>
/// Error handling: 4xx responses indicate adapter / config bugs (wrong
/// package path, malformed request) and throw
/// <see cref="InvalidOperationException"/>. 5xx responses, timeouts,
/// network errors, and malformed result shapes route through the
/// configured <see cref="OpaFailMode"/> — <c>Closed</c> (default)
/// denies with the failure reason; <c>Open</c> allows.
/// </para>
/// <para>
/// Caching: <see cref="DecisionCache"/> memoises decisions by SHA-256
/// of the canonical-JSON input. TTL defaults to 5s; disable with
/// <see cref="OpaPolicyEngineOptions.DecisionCacheTtl"/> = zero.
/// </para>
/// </remarks>
public sealed class OpaPolicyEngine : IAgentPolicyEngine
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<OpaPolicyEngineOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpaPolicyEngine> _logger;
    private readonly DecisionCache _cache;
    private int _policyVersionLogged;

    /// <summary>Construct a policy engine bound to a typed <see cref="HttpClient"/>.</summary>
    public OpaPolicyEngine(
        HttpClient httpClient,
        IOptionsMonitor<OpaPolicyEngineOptions> options,
        TimeProvider timeProvider,
        ILogger<OpaPolicyEngine> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var initialOptions = _options.CurrentValue;
        _cache = new DecisionCache(_timeProvider, initialOptions.DecisionCacheTtl, initialOptions.DecisionCacheMaxEntries);
    }

    /// <inheritdoc />
    public async ValueTask<PolicyDecision> EvaluateAsync(
        PolicyOperation operation,
        AgentManifest? manifest,
        AgentPrincipal? principal,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        var inputNode = OpaInputBuilder.Build(operation, manifest, principal);
        var inputJson = inputNode.ToJsonString(OpaInputBuilder.SerializerOptions);
        var cacheKey = DecisionCache.ComputeKey(inputJson);

        if (_cache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        using var timeoutCts = new CancellationTokenSource(options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            var requestBody = new { input = inputNode };
            response = await _httpClient
                .PostAsJsonAsync(BuildDataPath(options), requestBody, OpaInputBuilder.SerializerOptions, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ApplyFailMode(options, "OPA request timed out");
        }
        catch (HttpRequestException ex)
        {
            return ApplyFailMode(options, $"OPA request failed: {ex.Message}");
        }

        using (response)
        {
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"OPA returned {(int)response.StatusCode} — likely wrong DataPath ('{options.DataPath}') or malformed request. Response body: {body}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ApplyFailMode(options, $"OPA returned {(int)response.StatusCode}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = OpaResponseParser.Parse(responseBody);
            if (parsed is null)
            {
                _logger.LogWarning("OPA returned an unparseable result body: {Body}", Truncate(responseBody, 500));
                return ApplyFailMode(options, "OPA returned malformed result");
            }

            _cache.Set(cacheKey, parsed.Value);
            _ = MaybeLogPolicyVersionAsync();
            return parsed.Value;
        }
    }

    private string BuildDataPath(OpaPolicyEngineOptions options)
    {
        var trimmed = options.DataPath.TrimStart('/');
        return $"/v1/data/{trimmed}";
    }

    private PolicyDecision ApplyFailMode(OpaPolicyEngineOptions options, string reason)
    {
        _logger.LogWarning("OPA evaluation failed — applying FailMode={FailMode}. Reason: {Reason}", options.FailMode, reason);
        return options.FailMode == OpaFailMode.Open
            ? PolicyDecision.Allow
            : PolicyDecision.Deny(reason);
    }

    private Task MaybeLogPolicyVersionAsync()
    {
        var options = _options.CurrentValue;
        if (!options.LogPolicyVersionOnStartup)
        {
            return Task.CompletedTask;
        }
        if (Interlocked.Exchange(ref _policyVersionLogged, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                using var response = await _httpClient.GetAsync("/v1/status").ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("OPA /v1/status returned {StatusCode}; skipping policy-version log.", (int)response.StatusCode);
                    return;
                }
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("bundles", out var bundles))
                {
                    _logger.LogInformation("OPA policy bundles: {Bundles}", bundles.GetRawText());
                }
                else
                {
                    _logger.LogInformation("OPA /v1/status probe succeeded (no bundles reported).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OPA /v1/status probe failed; continuing without policy-version log.");
            }
        });
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
