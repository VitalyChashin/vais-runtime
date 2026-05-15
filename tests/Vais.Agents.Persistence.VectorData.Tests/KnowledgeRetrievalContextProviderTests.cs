// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Persistence.VectorData.Tests;

public sealed class KnowledgeRetrievalContextProviderTests
{
    [Fact]
    public async Task Contributes_SystemPromptAddendum_With_Retrieved_Chunks()
    {
        var retriever = new StubRetriever(new[]
        {
            new KnowledgeChunk("chunk-one"),
            new KnowledgeChunk("chunk-two"),
        });
        var provider = new KnowledgeRetrievalContextProvider(retriever);

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.User, "What do you know?") },
            systemPrompt: "Be brief.");

        var contribution = await provider.InvokeAsync(invocation);

        contribution.SystemPromptAddendum.Should().NotBeNull();
        contribution.SystemPromptAddendum!.Should().Contain("Relevant context:");
        contribution.SystemPromptAddendum.Should().Contain("chunk-one");
        contribution.SystemPromptAddendum.Should().Contain("chunk-two");
        contribution.InjectedHistory.Should().BeNull();
        contribution.AdditionalTools.Should().BeNull();
        retriever.Queries.Should().ContainSingle().Which.Should().Be("What do you know?");
    }

    [Fact]
    public async Task Contributes_Nothing_When_No_User_Message_In_History()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("should-not-appear") });
        var provider = new KnowledgeRetrievalContextProvider(retriever);

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.System, "sys") },
            systemPrompt: "Original.");

        var contribution = await provider.InvokeAsync(invocation);

        contribution.Should().BeSameAs(ContextContribution.Empty);
        retriever.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task Contributes_Nothing_When_Retriever_Returns_Zero_Chunks()
    {
        var retriever = new StubRetriever(Array.Empty<KnowledgeChunk>());
        var provider = new KnowledgeRetrievalContextProvider(retriever);

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.User, "anything") },
            systemPrompt: "Original.");

        var contribution = await provider.InvokeAsync(invocation);

        contribution.Should().BeSameAs(ContextContribution.Empty);
        retriever.Queries.Should().ContainSingle().Which.Should().Be("anything");
    }

    [Fact]
    public async Task Addendum_Is_Just_The_Context_Block_When_No_Base_SystemPrompt()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("fact") });
        var provider = new KnowledgeRetrievalContextProvider(retriever);

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.User, "q") },
            systemPrompt: null);

        var contribution = await provider.InvokeAsync(invocation);

        contribution.SystemPromptAddendum.Should().StartWith("Relevant context:");
        contribution.SystemPromptAddendum.Should().Contain("fact");
    }

    [Fact]
    public async Task Respects_Custom_Template_And_Separator()
    {
        var retriever = new StubRetriever(new[]
        {
            new KnowledgeChunk("a"),
            new KnowledgeChunk("b"),
        });
        var provider = new KnowledgeRetrievalContextProvider(retriever, new KnowledgeRetrievalOptions
        {
            Template = "KB:\n{chunks}\nEND",
            ChunkSeparator = " | ",
        });

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.User, "q") },
            systemPrompt: null);

        var contribution = await provider.InvokeAsync(invocation);

        contribution.SystemPromptAddendum.Should().Be("KB:\na | b\nEND");
    }

    [Fact]
    public async Task Emits_Retrieval_Section_With_Default_Id_And_Priority_5()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("fact") });
        var provider = new KnowledgeRetrievalContextProvider(retriever);

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.User, "q") },
            systemPrompt: null);

        var contribution = await provider.InvokeAsync(invocation);

        contribution.Sections.Should().ContainSingle();
        var section = contribution.Sections[0];
        section.Id.Should().Be("retrieval.docs");
        section.Kind.Should().Be(SectionKind.SystemSegment);
        section.ProducerId.Should().Be(nameof(KnowledgeRetrievalContextProvider));
        section.Budget.Should().NotBeNull();
        section.Budget!.Priority.Should().Be(5);
        section.Payload.Should().BeOfType<TextPayload>()
            .Which.Value.Should().Contain("fact");
    }

    [Fact]
    public async Task Custom_SectionId_Option_Is_Honored_For_Multi_Retriever_Scenarios()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("fact") });
        var provider = new KnowledgeRetrievalContextProvider(retriever, new KnowledgeRetrievalOptions
        {
            SectionId = "retrieval.support_kb",
        });

        var invocation = BuildInvocation(
            history: new[] { new ChatTurn(AgentChatRole.User, "q") },
            systemPrompt: null);

        var contribution = await provider.InvokeAsync(invocation);

        contribution.Sections.Should().ContainSingle()
            .Which.Id.Should().Be("retrieval.support_kb");
    }

    [Fact]
    public async Task Uses_Most_Recent_User_Turn_As_Query_When_History_Has_Assistant_In_Between()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("x") });
        var provider = new KnowledgeRetrievalContextProvider(retriever);

        var invocation = BuildInvocation(
            history: new[]
            {
                new ChatTurn(AgentChatRole.User, "first-question"),
                new ChatTurn(AgentChatRole.Assistant, "first-answer"),
                new ChatTurn(AgentChatRole.User, "latest-question"),
            },
            systemPrompt: null);

        await provider.InvokeAsync(invocation);

        retriever.Queries.Should().ContainSingle().Which.Should().Be("latest-question");
    }

    private static ContextInvocationContext BuildInvocation(IReadOnlyList<ChatTurn> history, string? systemPrompt)
    {
        var candidate = new CompletionRequest(history, systemPrompt);
        var session = new Vais.Agents.Core.InMemoryAgentSession("agent-t", sessionId: "s");
        return new ContextInvocationContext(candidate, AgentContext.Empty, session);
    }

    private sealed class StubRetriever : IKnowledgeRetriever
    {
        private readonly IReadOnlyList<KnowledgeChunk> _chunks;
        public List<string> Queries { get; } = new();

        public StubRetriever(IReadOnlyList<KnowledgeChunk> chunks) => _chunks = chunks;

        public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(_chunks);
        }
    }
}
