// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class CompletionResponseTests
{
    [Fact]
    public void TotalTokens_Is_Null_When_Both_Parts_Null()
    {
        new CompletionResponse("t").TotalTokens.Should().BeNull();
    }

    [Fact]
    public void TotalTokens_Sums_When_Both_Present()
    {
        new CompletionResponse("t", PromptTokens: 10, CompletionTokens: 4)
            .TotalTokens.Should().Be(14);
    }

    [Fact]
    public void TotalTokens_Treats_Missing_Part_As_Zero()
    {
        new CompletionResponse("t", PromptTokens: 5, CompletionTokens: null)
            .TotalTokens.Should().Be(5);

        new CompletionResponse("t", PromptTokens: null, CompletionTokens: 7)
            .TotalTokens.Should().Be(7);
    }
}
