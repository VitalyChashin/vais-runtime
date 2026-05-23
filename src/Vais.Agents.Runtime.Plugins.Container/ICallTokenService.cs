// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// HMAC-SHA256 implementation of <see cref="ICallTokenService"/>.
/// Token format: base64url(payload).base64url(hmac).
/// Payload is one of two colon-delimited shapes:
///   v1 (short-turn): <c>{runId}:{agentId}:{expiresAtUnixSeconds}</c>
///   v2 (session):    <c>v2:{runId}:{agentId}:{leaseId}:{expiresAtUnixSeconds}</c>
/// runId / agentId / leaseId never contain a colon (GUIDs and manifest ids), so a plain split is safe.
/// </summary>
internal sealed class HmacCallTokenService : ICallTokenService
{
    private const string V2Prefix = "v2";
    private readonly byte[] _keyBytes;

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

    public bool Validate(string token, string runId, string agentId) =>
        TryExtract(token, out var r, out var a) && r == runId && a == agentId;

    public bool TryExtract(string token, out string runId, out string agentId) =>
        TryExtract(token, out runId, out agentId, out _);

    public bool TryExtract(string token, out string runId, out string agentId, out string leaseId)
    {
        runId = agentId = leaseId = string.Empty;
        try
        {
            var dot = token.IndexOf('.');
            if (dot < 0) return false;

            var payloadBytes = Base64UrlDecode(token[..dot]);
            var tokenHmac = Base64UrlDecode(token[(dot + 1)..]);

            var expectedHmac = HMACSHA256.HashData(_keyBytes, payloadBytes);
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
