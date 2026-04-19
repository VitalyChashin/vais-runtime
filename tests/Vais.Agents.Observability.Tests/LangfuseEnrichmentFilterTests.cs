// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Vais.Agents.Core;
using Vais.Agents.Observability.Langfuse;
using Xunit;

namespace Vais.Agents.Observability.Tests;

/// <summary>
/// Verifies the neutral Langfuse enricher sets <c>langfuse.*</c> tags on the active
/// Activity when context is present, and that enrichment never throws.
/// </summary>
public sealed class LangfuseEnrichmentFilterTests
{
    [Fact]
    public async Task Sets_User_And_Session_Tags_From_Context()
    {
        var recorded = new List<Activity>();
        using var listener = ActivityEmissionTests.SubscribeTo(recorded, AgenticDiagnostics.ActivitySourceName);

        var accessor = new AsyncLocalAgentContextAccessor();
        using var _ = accessor.Push(new AgentContext(
            UserId: "u-123",
            TenantId: "tenant-a",
            CorrelationId: "session-xyz",
            AgentName: "support-bot"));

        var filter = new LangfuseEnrichmentFilter(accessor);
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ContextAccessor = accessor,
            Filters = new[] { (IAgentFilter)filter },
        });

        await agent.AskAsync("hi");

        var tags = recorded.Single().TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags[LangfuseTags.UserId].Should().Be("u-123");
        tags[LangfuseTags.SessionId].Should().Be("session-xyz");
        tags[LangfuseTags.TraceName].Should().Be("support-bot");
        tags[LangfuseTags.MetadataPrefix + "agent_name"].Should().Be("support-bot");
        tags[LangfuseTags.MetadataPrefix + "tenant_id"].Should().Be("tenant-a");
        tags[LangfuseTags.MetadataPrefix + "correlation_id"].Should().Be("session-xyz");
        tags[LangfuseTags.Tags].Should().BeEquivalentTo(new[] { "vais-agents" });
    }

    [Fact]
    public async Task Falls_Back_To_AnonymousUserId_When_Context_Missing()
    {
        var recorded = new List<Activity>();
        using var listener = ActivityEmissionTests.SubscribeTo(recorded, AgenticDiagnostics.ActivitySourceName);

        var accessor = new AsyncLocalAgentContextAccessor(); // empty context
        var options = new LangfuseEnrichmentOptions { AnonymousUserId = "anonymous" };
        var filter = new LangfuseEnrichmentFilter(accessor, options);

        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ContextAccessor = accessor,
            Filters = new[] { (IAgentFilter)filter },
        });

        await agent.AskAsync("hi");

        var tags = recorded.Single().TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags[LangfuseTags.UserId].Should().Be("anonymous");
        tags.Should().NotContainKey(LangfuseTags.SessionId);
    }

    [Fact]
    public async Task Attaches_Static_Metadata()
    {
        var recorded = new List<Activity>();
        using var listener = ActivityEmissionTests.SubscribeTo(recorded, AgenticDiagnostics.ActivitySourceName);

        var accessor = new AsyncLocalAgentContextAccessor();
        var options = new LangfuseEnrichmentOptions
        {
            StaticMetadata = new Dictionary<string, string>
            {
                ["environment"] = "staging",
                ["service"] = "agentic-runtime",
            },
            DefaultTags = new[] { "unit-test", "m2b" },
        };
        var filter = new LangfuseEnrichmentFilter(accessor, options);

        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Filters = new[] { (IAgentFilter)filter },
        });

        await agent.AskAsync("hi");

        var tags = recorded.Single().TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags[LangfuseTags.MetadataPrefix + "environment"].Should().Be("staging");
        tags[LangfuseTags.MetadataPrefix + "service"].Should().Be("agentic-runtime");
        tags[LangfuseTags.Tags].Should().BeEquivalentTo(new[] { "unit-test", "m2b" });
    }

    [Fact]
    public async Task Does_Not_Throw_When_No_Activity_Current()
    {
        // Filter must be safe to call in contexts where no ActivitySource listener is
        // attached. StatefulAiAgent's StartActivity() returns null in that case, and the
        // filter should quietly skip enrichment.
        var accessor = new AsyncLocalAgentContextAccessor();
        var filter = new LangfuseEnrichmentFilter(accessor);

        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            Filters = new[] { (IAgentFilter)filter },
        });

        var reply = await agent.AskAsync("hi");
        reply.Should().Be("ok");
    }
}
