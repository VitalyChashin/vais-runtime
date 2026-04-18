// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Orleans.Runtime;
using Vais2.Agents.Core;
using Xunit;

namespace Vais2.Agents.Hosting.Orleans.Tests;

public sealed class OrleansAgentContextAccessorTests
{
    [Fact]
    public void Empty_RequestContext_Yields_All_Null_Fields()
    {
        RequestContext.Clear();
        var accessor = new OrleansAgentContextAccessor();

        var context = accessor.Current;

        context.UserId.Should().BeNull();
        context.TenantId.Should().BeNull();
        context.CorrelationId.Should().BeNull();
        context.AgentName.Should().BeNull();
    }

    [Fact]
    public void RequestContext_Values_Surface_As_AgentContext_Fields()
    {
        RequestContext.Clear();
        RequestContext.Set(AgenticTags.UserId, "user-42");
        RequestContext.Set(AgenticTags.TenantId, "tenant-blue");
        RequestContext.Set(AgenticTags.CorrelationId, "corr-abc");
        RequestContext.Set(AgenticTags.AgentName, "weather-bot");

        var context = new OrleansAgentContextAccessor().Current;

        context.UserId.Should().Be("user-42");
        context.TenantId.Should().Be("tenant-blue");
        context.CorrelationId.Should().Be("corr-abc");
        context.AgentName.Should().Be("weather-bot");

        RequestContext.Clear();
    }

    [Fact]
    public void Non_String_Values_Are_Ignored_Not_Thrown()
    {
        RequestContext.Clear();
        RequestContext.Set(AgenticTags.UserId, 12345); // wrong type on purpose

        var context = new OrleansAgentContextAccessor().Current;
        context.UserId.Should().BeNull();

        RequestContext.Clear();
    }
}
