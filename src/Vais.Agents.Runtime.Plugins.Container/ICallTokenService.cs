// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// HMAC-SHA256 implementation of <see cref="ICallTokenService"/>.
/// Token format: base64url(payload).base64url(hmac)
/// Payload: {runId}:{agentId}:{expiresAtUnixSeconds}
/// </summary>
internal sealed class HmacCallTokenService : ICallTokenService
{
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

    public string Generate(string runId, string agentId, int timeoutSeconds)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds + 30).ToUnixTimeSeconds();
        var payload = $"{runId}:{agentId}:{expiresAt}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = HMACSHA256.HashData(_keyBytes, payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(hmac)}";
    }

    public bool Validate(string token, string runId, string agentId)
    {
        try
        {
            var dot = token.IndexOf('.');
            if (dot < 0) return false;

            var payloadBytes = Base64UrlDecode(token[..dot]);
            var tokenHmac = Base64UrlDecode(token[(dot + 1)..]);

            var expectedHmac = HMACSHA256.HashData(_keyBytes, payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(tokenHmac, expectedHmac)) return false;

            var payload = Encoding.UTF8.GetString(payloadBytes);
            var parts = payload.Split(':');
            if (parts.Length != 3) return false;
            if (parts[0] != runId || parts[1] != agentId) return false;
            if (!long.TryParse(parts[2], out var expiresAt)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt) return false;

            return true;
        }
        catch
        {
            return false;
        }
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
