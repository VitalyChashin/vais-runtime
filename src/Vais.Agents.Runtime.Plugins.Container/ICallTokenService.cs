// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// HMAC-SHA256 implementation of <see cref="ICallTokenService"/>.
/// Token format: base64url(payload).[base64url(claims-json).]base64url(hmac).
/// The middle claims segment is present for v3 (claims-bearing) tokens minted after G4 — legacy
/// two-segment v1/v2 tokens parse with <c>claims == null</c> for backwards compatibility during
/// rollout. HMAC covers <c>payloadBytes ⊕ '.' ⊕ claimsBytes</c> for v3 tokens, or just
/// <c>payloadBytes</c> for legacy tokens.
/// Payload is one of two colon-delimited shapes:
///   v1 (short-turn): <c>{runId}:{agentId}:{expiresAtUnixSeconds}</c>
///   v2 (session):    <c>v2:{runId}:{agentId}:{leaseId}:{expiresAtUnixSeconds}</c>
/// runId / agentId / leaseId never contain a colon (GUIDs and manifest ids), so a plain split is safe.
/// </summary>
internal sealed class HmacCallTokenService : ICallTokenService
{
    private const string V2Prefix = "v2";
    private readonly byte[] _keyBytes;

    private static readonly JsonSerializerOptions ClaimsJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public HmacCallTokenService(IConfiguration configuration)
    {
        var secret = configuration["Vais:ContainerPlugin:CallTokenSecret"]
            ?? throw new InvalidOperationException(
                "Vais:ContainerPlugin:CallTokenSecret is required when container plugins are enabled.");
        if (secret.Length < 32)
            throw new InvalidOperationException(
                "Vais:ContainerPlugin:CallTokenSecret must be at least 32 characters.");
        _keyBytes = Encoding.UTF8.GetBytes(secret);
    }

    public string Generate(string runId, string agentId, int ttlSeconds) =>
        Sign($"{runId}:{agentId}:{Expiry(ttlSeconds)}");

    public string Generate(string runId, string agentId, string leaseId, int ttlSeconds) =>
        Sign($"{V2Prefix}:{runId}:{agentId}:{leaseId}:{Expiry(ttlSeconds)}");

    public string Generate(string runId, string agentId, AgentContextClaims claims, int ttlSeconds) =>
        SignWithClaims($"{runId}:{agentId}:{Expiry(ttlSeconds)}", claims);

    public string Generate(string runId, string agentId, string leaseId, AgentContextClaims claims, int ttlSeconds) =>
        SignWithClaims($"{V2Prefix}:{runId}:{agentId}:{leaseId}:{Expiry(ttlSeconds)}", claims);

    public bool Validate(string token, string runId, string agentId) =>
        TryExtract(token, out var r, out var a) && r == runId && a == agentId;

    public bool TryExtract(string token, out string runId, out string agentId) =>
        TryExtract(token, out runId, out agentId, out _, out _);

    public bool TryExtract(string token, out string runId, out string agentId, out string leaseId) =>
        TryExtract(token, out runId, out agentId, out leaseId, out _);

    public bool TryExtract(string token, out string runId, out string agentId, out string leaseId, out AgentContextClaims? claims)
    {
        runId = agentId = leaseId = string.Empty;
        claims = null;
        try
        {
            var firstDot = token.IndexOf('.');
            if (firstDot < 0) return false;
            var secondDot = token.IndexOf('.', firstDot + 1);

            byte[] payloadBytes;
            byte[] tokenHmac;
            byte[] signedMaterial;
            byte[]? claimsBytes = null;

            if (secondDot < 0)
            {
                // Legacy 2-segment token: payload.hmac. HMAC covers payload only.
                payloadBytes = Base64UrlDecode(token[..firstDot]);
                tokenHmac = Base64UrlDecode(token[(firstDot + 1)..]);
                signedMaterial = payloadBytes;
            }
            else
            {
                // v3 3-segment token: payload.claims.hmac. HMAC covers payload + '.' + claims
                // (decoded bytes — independent of base64url padding choices on the wire).
                payloadBytes = Base64UrlDecode(token[..firstDot]);
                claimsBytes = Base64UrlDecode(token[(firstDot + 1)..secondDot]);
                tokenHmac = Base64UrlDecode(token[(secondDot + 1)..]);

                signedMaterial = new byte[payloadBytes.Length + 1 + claimsBytes.Length];
                payloadBytes.CopyTo(signedMaterial, 0);
                signedMaterial[payloadBytes.Length] = (byte)'.';
                claimsBytes.CopyTo(signedMaterial, payloadBytes.Length + 1);
            }

            var expectedHmac = HMACSHA256.HashData(_keyBytes, signedMaterial);
            if (!CryptographicOperations.FixedTimeEquals(tokenHmac, expectedHmac)) return false;

            var parts = Encoding.UTF8.GetString(payloadBytes).Split(':');
            string r, a, lease = string.Empty;
            long expiresAt;
            if (parts.Length == 5 && parts[0] == V2Prefix)
            {
                if (!long.TryParse(parts[4], out expiresAt)) return false;
                r = parts[1]; a = parts[2]; lease = parts[3];
            }
            else if (parts.Length == 3)
            {
                if (!long.TryParse(parts[2], out expiresAt)) return false;
                r = parts[0]; a = parts[1];
            }
            else
            {
                return false;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt) return false;

            if (claimsBytes is not null)
            {
                try
                {
                    claims = JsonSerializer.Deserialize<AgentContextClaims>(claimsBytes, ClaimsJsonOptions);
                }
                catch (JsonException)
                {
                    // Malformed claims segment — HMAC passed so it's syntactically intentional, but
                    // semantically broken. Fail closed: reject the token.
                    return false;
                }
            }

            runId = r;
            agentId = a;
            leaseId = lease;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long Expiry(int ttlSeconds) =>
        DateTimeOffset.UtcNow.AddSeconds(ttlSeconds).ToUnixTimeSeconds();

    private string Sign(string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = HMACSHA256.HashData(_keyBytes, payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(hmac)}";
    }

    private string SignWithClaims(string payload, AgentContextClaims claims)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var claimsBytes = JsonSerializer.SerializeToUtf8Bytes(claims, ClaimsJsonOptions);

        var signedMaterial = new byte[payloadBytes.Length + 1 + claimsBytes.Length];
        payloadBytes.CopyTo(signedMaterial, 0);
        signedMaterial[payloadBytes.Length] = (byte)'.';
        claimsBytes.CopyTo(signedMaterial, payloadBytes.Length + 1);
        var hmac = HMACSHA256.HashData(_keyBytes, signedMaterial);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(claimsBytes)}.{Base64UrlEncode(hmac)}";
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
