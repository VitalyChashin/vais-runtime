// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Orleans serialisation surrogate for <see cref="ChatTurn"/>. The Abstractions package
/// is intentionally Orleans-free, so the serialiser lives here alongside its converter.
/// </summary>
[GenerateSerializer]
public struct ChatTurnSurrogate
{
    /// <summary>Role of the turn speaker.</summary>
    [Id(0)]
    public AgentChatRole Role;

    /// <summary>Turn text content.</summary>
    [Id(1)]
    public string Text;
}

/// <summary>
/// Converts between <see cref="ChatTurn"/> (public record in Abstractions) and its
/// Orleans-serialisable surrogate. Registered automatically via <see cref="RegisterConverterAttribute"/>.
/// </summary>
[RegisterConverter]
public sealed class ChatTurnSurrogateConverter : IConverter<ChatTurn, ChatTurnSurrogate>
{
    /// <inheritdoc />
    public ChatTurn ConvertFromSurrogate(in ChatTurnSurrogate surrogate) =>
        new(surrogate.Role, surrogate.Text);

    /// <inheritdoc />
    public ChatTurnSurrogate ConvertToSurrogate(in ChatTurn value) =>
        new() { Role = value.Role, Text = value.Text };
}
