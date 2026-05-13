// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class IdempotencyKeyFactoryTests
{
    [Fact]
    public void Build_IncludesUidGenerationVerb()
    {
        var key = IdempotencyKeyFactory.Build("abc-uid", 7, IdempotencyKeyFactory.CreateVerb);
        key.Should().Be("abc-uid:7:create");
    }

    [Fact]
    public void Build_SameArgs_ProducesSameKey()
    {
        IdempotencyKeyFactory.Build("u", 1, "update")
            .Should().Be(IdempotencyKeyFactory.Build("u", 1, "update"));
    }

    [Fact]
    public void Build_DifferentGeneration_ProducesDifferentKey()
    {
        IdempotencyKeyFactory.Build("u", 1, "update")
            .Should().NotBe(IdempotencyKeyFactory.Build("u", 2, "update"));
    }

    [Fact]
    public void Build_DifferentVerb_ProducesDifferentKey()
    {
        IdempotencyKeyFactory.Build("u", 1, "create")
            .Should().NotBe(IdempotencyKeyFactory.Build("u", 1, "update"));
    }

    [Fact]
    public void Build_EmptyUid_Throws()
    {
        FluentActions.Invoking(() => IdempotencyKeyFactory.Build(string.Empty, 1, "create"))
            .Should().Throw<ArgumentException>();
    }
}
