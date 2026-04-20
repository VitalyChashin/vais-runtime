// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// Maps an OPA <c>/v1/data/*</c> response body to a
/// <see cref="PolicyDecision"/>. OPA returns
/// <c>{"result": &lt;boolean-or-object&gt;}</c>:
/// <list type="bullet">
///   <item><description><c>{"result": true}</c> → <see cref="PolicyDecision.Allow"/>.</description></item>
///   <item><description><c>{"result": false}</c> → <see cref="PolicyDecision.Deny"/> with generic reason.</description></item>
///   <item><description><c>{"result": {"allowed": true, "reason": "..."}}</c> → <see cref="PolicyDecision.Allow"/> (reason ignored when allowed).</description></item>
///   <item><description><c>{"result": {"allowed": false, "reason": "..."}}</c> → <see cref="PolicyDecision.Deny"/> with the supplied reason.</description></item>
///   <item><description>Any other shape → <c>null</c>; caller applies FailMode.</description></item>
/// </list>
/// </summary>
internal static class OpaResponseParser
{
    /// <summary>Generic deny reason when the policy returns <c>{"result": false}</c> without a structured explanation.</summary>
    public const string DefaultDenyReason = "Policy denied";

    /// <summary>
    /// Parse <paramref name="body"/> into a decision. Returns
    /// <c>null</c> on malformed or unsupported shapes — the caller
    /// treats that as a runtime failure and applies FailMode.
    /// </summary>
    public static PolicyDecision? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result))
            {
                return null;
            }

            switch (result.ValueKind)
            {
                case JsonValueKind.True:
                    return PolicyDecision.Allow;
                case JsonValueKind.False:
                    return PolicyDecision.Deny(DefaultDenyReason);
                case JsonValueKind.Object:
                    return ParseObjectResult(result);
                default:
                    return null;
            }
        }
    }

    private static PolicyDecision? ParseObjectResult(JsonElement obj)
    {
        if (!obj.TryGetProperty("allowed", out var allowed) || allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        if (allowed.ValueKind == JsonValueKind.True)
        {
            return PolicyDecision.Allow;
        }

        string reason = DefaultDenyReason;
        if (obj.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
        {
            var r = reasonElement.GetString();
            if (!string.IsNullOrWhiteSpace(r))
            {
                reason = r;
            }
        }
        return PolicyDecision.Deny(reason);
    }
}
