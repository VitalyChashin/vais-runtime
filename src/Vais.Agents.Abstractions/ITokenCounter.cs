// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Counts the number of tokens a given text encodes to. Used by section budgeting when the
/// model's wire budget is measured in tokens; otherwise the packer falls back to character
/// counting. Implementations are expected to be stateless and thread-safe.
/// </summary>
/// <remarks>
/// Tokenizers ship in separate packages (e.g. cl100k_base, o200k_base, gemini). Vais.Agents.Core
/// ships no concrete implementation in SC-5; consumers wire one in when the model spec advertises
/// a tokenizer.
/// </remarks>
public interface ITokenCounter
{
    /// <summary>Count the number of tokens <paramref name="text"/> encodes to.</summary>
    /// <param name="text">Text to measure. Must not be null.</param>
    /// <returns>The token count; never negative.</returns>
    int Count(string text);
}
