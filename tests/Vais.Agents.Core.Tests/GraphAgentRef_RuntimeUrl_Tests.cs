// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class GraphAgentRef_RuntimeUrl_Tests
{
    [Fact]
    public void DefaultConstructor_RuntimeUrlIsNull()
    {
        var r = new GraphAgentRef("agent-1", "1.0");

        r.RuntimeUrl.Should().BeNull();
    }

    [Fact]
    public void RuntimeUrl_IsPreserved_WhenSupplied()
    {
        var r = new GraphAgentRef("agent-1", "1.0", "https://runtime-b.svc.cluster.local");

        r.RuntimeUrl.Should().Be("https://runtime-b.svc.cluster.local");
    }
}
