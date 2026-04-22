// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Response model for an RFC 8693 OAuth 2.0 Token Exchange response from the STS.
/// </summary>
internal sealed class OidcTokenExchangeResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = string.Empty;

    [JsonPropertyName("issued_token_type")]
    public string? IssuedTokenType { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}
