// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Protocols.A2A.Tests;

public sealed class A2AGraphNodeInvokerTests
{
    [Fact]
    public void ExtractText_MessageResponse_ReturnsText()
    {
        var message = new Message { Role = Role.Agent, MessageId = "1" };
        message.Parts.Add(Part.FromText("hello from A2A"));

        var response = new SendMessageResponse { Message = message };

        var result = A2AGraphNodeInvoker.ExtractText(response, "https://a2a.svc");

        result.Should().Be("hello from A2A");
    }

    [Fact]
    public void ExtractText_MessageResponse_MultipleParts_Concatenated()
    {
        var message = new Message { Role = Role.Agent, MessageId = "1" };
        message.Parts.Add(Part.FromText("part-1"));
        message.Parts.Add(Part.FromText("part-2"));

        var response = new SendMessageResponse { Message = message };

        var result = A2AGraphNodeInvoker.ExtractText(response, "https://a2a.svc");

        result.Should().Be("part-1\npart-2");
    }

    [Fact]
    public void ExtractText_TaskResponse_ExtractsArtifactText()
    {
        var task = new AgentTask { Id = "t1", Status = new global::A2A.TaskStatus { State = TaskState.Completed } };
        var artifact = new Artifact { ArtifactId = "a1" };
        artifact.Parts.Add(Part.FromText("artifact-text"));
        task.Artifacts = [artifact];

        var response = new SendMessageResponse { Task = task };

        var result = A2AGraphNodeInvoker.ExtractText(response, "https://a2a.svc");

        result.Should().Be("artifact-text");
    }

    [Fact]
    public void ExtractText_EmptyMessage_Throws()
    {
        var message = new Message { Role = Role.Agent, MessageId = "1" };
        var response = new SendMessageResponse { Message = message };

        var act = () => A2AGraphNodeInvoker.ExtractText(response, "https://a2a.svc");

        act.Should().Throw<A2AAgentInvocationException>()
            .WithMessage("*no text parts*");
    }

    [Fact]
    public void ExtractText_EmptyTask_Throws()
    {
        var task = new AgentTask { Id = "t1", Status = new global::A2A.TaskStatus { State = TaskState.Completed } };
        var response = new SendMessageResponse { Task = task };

        var act = () => A2AGraphNodeInvoker.ExtractText(response, "https://a2a.svc");

        act.Should().Throw<A2AAgentInvocationException>()
            .WithMessage("*no text artifacts*");
    }

    [Fact]
    public async Task InvokeAsync_Rejects_Null_Url()
    {
        var sut = new A2AGraphNodeInvoker();

        var act = async () => await sut.InvokeAsync(null!, "msg", null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InvokeAsync_Rejects_Empty_Url()
    {
        var sut = new A2AGraphNodeInvoker();

        var act = async () => await sut.InvokeAsync("  ", "msg", null);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
