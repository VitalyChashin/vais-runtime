// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// A single message in a conversation. Immutable, value-equal, serialisable.
/// </summary>
/// <param name="Role">Who produced this turn.</param>
/// <param name="Text">The message content. Non-null; may be empty.</param>
public sealed record ChatTurn(AgentChatRole Role, string Text);
