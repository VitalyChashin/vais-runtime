// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Persistence.VectorData.Tests;

// KnowledgeRetrievalFilter is [Obsolete] as of v0.4. Tests stay live to prove the
// filter keeps working for one release window; removal planned for v0.5.
#pragma warning disable VAIS0001

public sealed class KnowledgeRetrievalFilterTests
{
    [Fact]
    public async Task Augments_System_Prompt_With_Retrieved_Chunks()
    {
        var retriever = new StubRetriever(new[]
        {
            new KnowledgeChunk("chunk-one"),
            new KnowledgeChunk("chunk-two"),
        });
        var filter = new KnowledgeRetrievalFilter(retriever);

        var request = new CompletionRequest(
            History: new[] { new ChatTurn(AgentChatRole.User, "What do you know?") },
            SystemPrompt: "Be brief.");

        CompletionRequest? observed = null;
        await filter.InvokeAsync(
            request,
            (req, _) => { observed = req; return Task.FromResult(new CompletionResponse("ok")); },
            CancellationToken.None);

        observed.Should().NotBeNull();
        observed!.SystemPrompt.Should().NotBeNull();
        observed.SystemPrompt.Should().Contain("Be brief.");
        observed.SystemPrompt.Should().Contain("Relevant context:");
        observed.SystemPrompt.Should().Contain("chunk-one");
        observed.SystemPrompt.Should().Contain("chunk-two");
        retriever.Queries.Should().ContainSingle().Which.Should().Be("What do you know?");
    }

    [Fact]
    public async Task Passes_Through_When_No_User_Message_In_History()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("should-not-appear") });
        var filter = new KnowledgeRetrievalFilter(retriever);

        var request = new CompletionRequest(
            History: new[] { new ChatTurn(AgentChatRole.System, "sys") },
            SystemPrompt: "Original.");

        CompletionRequest? observed = null;
        await filter.InvokeAsync(
            request,
            (req, _) => { observed = req; return Task.FromResult(new CompletionResponse("ok")); },
            CancellationToken.None);

        observed.Should().BeSameAs(request);
        retriever.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task Passes_Through_When_Retriever_Returns_Zero_Chunks()
    {
        var retriever = new StubRetriever(Array.Empty<KnowledgeChunk>());
        var filter = new KnowledgeRetrievalFilter(retriever);

        var request = new CompletionRequest(
            History: new[] { new ChatTurn(AgentChatRole.User, "anything") },
            SystemPrompt: "Original.");

        CompletionRequest? observed = null;
        await filter.InvokeAsync(
            request,
            (req, _) => { observed = req; return Task.FromResult(new CompletionResponse("ok")); },
            CancellationToken.None);

        observed.Should().BeSameAs(request);
        retriever.Queries.Should().ContainSingle().Which.Should().Be("anything");
    }

    [Fact]
    public async Task Replaces_Null_SystemPrompt_With_Context_Block_Only()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("fact") });
        var filter = new KnowledgeRetrievalFilter(retriever);

        var request = new CompletionRequest(
            History: new[] { new ChatTurn(AgentChatRole.User, "q") },
            SystemPrompt: null);

        CompletionRequest? observed = null;
        await filter.InvokeAsync(
            request,
            (req, _) => { observed = req; return Task.FromResult(new CompletionResponse("ok")); },
            CancellationToken.None);

        observed!.SystemPrompt.Should().NotBeNull();
        observed.SystemPrompt.Should().StartWith("Relevant context:");
        observed.SystemPrompt.Should().Contain("fact");
    }

    [Fact]
    public async Task Respects_Custom_Template_And_Separator()
    {
        var retriever = new StubRetriever(new[]
        {
            new KnowledgeChunk("a"),
            new KnowledgeChunk("b"),
        });
        var filter = new KnowledgeRetrievalFilter(retriever, new KnowledgeRetrievalOptions
        {
            Template = "KB:\n{chunks}\nEND",
            ChunkSeparator = " | ",
        });

        var request = new CompletionRequest(
            History: new[] { new ChatTurn(AgentChatRole.User, "q") },
            SystemPrompt: null);

        CompletionRequest? observed = null;
        await filter.InvokeAsync(
            request,
            (req, _) => { observed = req; return Task.FromResult(new CompletionResponse("ok")); },
            CancellationToken.None);

        observed!.SystemPrompt.Should().Be("KB:\na | b\nEND");
    }

    [Fact]
    public async Task Uses_Most_Recent_User_Turn_As_Query_When_History_Has_Assistant_In_Between()
    {
        var retriever = new StubRetriever(new[] { new KnowledgeChunk("x") });
        var filter = new KnowledgeRetrievalFilter(retriever);

        var request = new CompletionRequest(
            History: new[]
            {
                new ChatTurn(AgentChatRole.User, "first-question"),
                new ChatTurn(AgentChatRole.Assistant, "first-answer"),
                new ChatTurn(AgentChatRole.User, "latest-question"),
            },
            SystemPrompt: null);

        await filter.InvokeAsync(
            request,
            (_, _) => Task.FromResult(new CompletionResponse("ok")),
            CancellationToken.None);

        retriever.Queries.Should().ContainSingle().Which.Should().Be("latest-question");
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
