// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Runs after every <see cref="IContextProvider"/> has contributed, to ensure the
/// resulting <see cref="CompletionRequest"/> fits whatever window the packer cares
/// about (model token budget, cost cap, quality-preserving truncation, etc.).
/// </summary>
/// <remarks>
/// <para>
/// The packer may drop turns, truncate content, or return the candidate unchanged.
/// It must preserve turn order and never append new turns — that's a
/// <see cref="IContextProvider"/> concern.
/// </para>
/// <para>
/// No model-window parameter is passed in; implementations own their own config
/// (target-token count, tokenizer choice, strategy). This keeps the contract small
/// and lets consumers swap strategies without touching <c>StatefulAiAgent</c>.
/// </para>
/// </remarks>
public interface IContextWindowPacker
{
    /// <summary>Return a request that fits the packer's target. Returning <paramref name="candidate"/> unchanged is valid.</summary>
    ValueTask<CompletionRequest> PackAsync(
        CompletionRequest candidate,
        CancellationToken cancellationToken = default);
}
