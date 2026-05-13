// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using A2A;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Protocols.A2A.Server.Tests;

/// <summary>
/// v0.8 PR 2: interrupt → <see cref="TaskState.InputRequired"/>, resume-via-taskId,
/// policy/budget → <see cref="TaskState.Failed"/>. Exercises the full wire via
/// <see cref="A2AClient"/> over <see cref="TestServer"/> so task persistence (via
/// the default <see cref="InMemoryTaskStore"/>) and state transitions are covered
/// end-to-end.
/// </summary>
public sealed class A2AAgentServerInterruptTests
{
    [Fact]
    public async Task Interrupt_Emits_InputRequired_Task_With_Data_Envelope()
    {
        using var host = await BuildHost(lifecycle => new ScriptedLifecycleManager(lifecycle,
            sequence: [ScriptedOutcome.Interrupt("int-1", "needs-approval", payload: "\"pending\"", runId: "run-9")]));
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        var response = await client.SendMessageAsync(BuildUserMessage("deploy now"), CancellationToken.None);

        response.PayloadCase.Should().Be(SendMessageResponseCase.Task);
        var task = response.Task!;
        task.Status.State.Should().Be(TaskState.InputRequired);
        var envelope = FindInterruptEnvelope(task);
        envelope.GetProperty("interruptId").GetString().Should().Be("int-1");
        envelope.GetProperty("reason").GetString().Should().Be("needs-approval");
        envelope.GetProperty("runId").GetString().Should().Be("run-9");
        envelope.GetProperty("agentId").GetString().Should().Be("echo");

        await host.StopAsync();
    }

    [Fact]
    public async Task Resume_Via_TaskId_Continues_Run_And_Completes()
    {
        var observedRequests = new List<AgentInvocationRequest>();
        using var host = await BuildHost(lifecycle => new ScriptedLifecycleManager(lifecycle,
            sequence: [
                ScriptedOutcome.Interrupt("int-1", "needs-approval", payload: "null", runId: "run-9"),
                ScriptedOutcome.Complete("continued-reply"),
            ],
            observer: observedRequests.Add));
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        // Call 1: triggers interrupt.
        var first = await client.SendMessageAsync(BuildUserMessage("deploy now"), CancellationToken.None);
        first.Task!.Status.State.Should().Be(TaskState.InputRequired);
        var taskId = first.Task.Id;

        // Call 2: resume with the taskId — should complete.
        var resumeMessage = BuildMessage("approved");
        resumeMessage.TaskId = taskId;
        var second = await client.SendMessageAsync(new SendMessageRequest { Message = resumeMessage }, CancellationToken.None);

        second.PayloadCase.Should().Be(SendMessageResponseCase.Task);
        second.Task!.Status.State.Should().Be(TaskState.Completed);

        // Resume metadata threaded through to the second InvokeAsync.
        observedRequests.Should().HaveCount(2);
        observedRequests[1].Metadata.Should().ContainKey("resume.interruptId").WhoseValue.Should().Be("int-1");
        observedRequests[1].Metadata.Should().ContainKey("resume.runId").WhoseValue.Should().Be("run-9");

        await host.StopAsync();
    }

    [Fact]
    public async Task Repeated_Interrupt_On_Resume_Stays_InputRequired()
    {
        using var host = await BuildHost(lifecycle => new ScriptedLifecycleManager(lifecycle,
            sequence: [
                ScriptedOutcome.Interrupt("int-1", "first", payload: "null"),
                ScriptedOutcome.Interrupt("int-2", "second", payload: "null"),
            ]));
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        var first = await client.SendMessageAsync(BuildUserMessage("deploy"), CancellationToken.None);
        var taskId = first.Task!.Id;

        var resumeMessage = BuildMessage("more-info");
        resumeMessage.TaskId = taskId;
        var second = await client.SendMessageAsync(new SendMessageRequest { Message = resumeMessage }, CancellationToken.None);

        second.Task!.Status.State.Should().Be(TaskState.InputRequired);
        var envelope = FindInterruptEnvelope(second.Task);
        envelope.GetProperty("interruptId").GetString().Should().Be("int-2");
        envelope.GetProperty("reason").GetString().Should().Be("second");

        await host.StopAsync();
    }

    [Fact]
    public async Task Policy_Denial_Emits_Failed_Task_With_Structured_Data()
    {
        using var host = await BuildHost(lifecycle => new ScriptedLifecycleManager(lifecycle,
            sequence: [ScriptedOutcome.PolicyDeny(PolicyOperation.Invoke, "not allowed in prod")]));
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        var response = await client.SendMessageAsync(BuildUserMessage("deploy"), CancellationToken.None);

        response.PayloadCase.Should().Be(SendMessageResponseCase.Task);
        response.Task!.Status.State.Should().Be(TaskState.Failed);
        var data = FindFailureDataPart(response.Task);
        data.GetProperty("code").GetString().Should().Be("policy-denied");
        data.GetProperty("operation").GetString().Should().Be("Invoke");
        data.GetProperty("reason").GetString().Should().Contain("not allowed in prod");

        await host.StopAsync();
    }

    [Fact]
    public async Task Budget_Exceeded_Emits_Failed_Task_With_Structured_Data()
    {
        using var host = await BuildHost(lifecycle => new ScriptedLifecycleManager(lifecycle,
            sequence: [ScriptedOutcome.BudgetExceeded("MaxTurns")]));
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        var response = await client.SendMessageAsync(BuildUserMessage("deploy"), CancellationToken.None);

        response.PayloadCase.Should().Be(SendMessageResponseCase.Task);
        response.Task!.Status.State.Should().Be(TaskState.Failed);
        var data = FindFailureDataPart(response.Task);
        data.GetProperty("code").GetString().Should().Be("budget-exceeded");
        data.GetProperty("field").GetString().Should().Be("MaxTurns");

        await host.StopAsync();
    }

    [Fact]
    public async Task Unknown_TaskId_Returns_A2A_Error()
    {
        using var host = await BuildHost();
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        var message = BuildMessage("resume");
        message.TaskId = "ghost-task-id";

        var act = async () => await client.SendMessageAsync(new SendMessageRequest { Message = message }, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<A2AException>()).Which;
        error.ErrorCode.Should().Be(A2AErrorCode.TaskNotFound);

        await host.StopAsync();
    }

    // ---- helpers ----

    private static SendMessageRequest BuildUserMessage(string text) =>
        new() { Message = BuildMessage(text) };

    private static Message BuildMessage(string text)
    {
        var msg = new Message
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
        };
        msg.Parts.Add(Part.FromText(text));
        return msg;
    }

    private static JsonElement FindInterruptEnvelope(AgentTask task)
    {
        // Status message carries the data-part on input-required. History may also have it.
        var candidate = task.Status.Message;
        if (candidate is not null)
        {
            foreach (var part in candidate.Parts)
            {
                if (part.ContentCase == PartContentCase.Data) return part.Data!.Value;
            }
        }
        if (task.History is not null)
        {
            foreach (var msg in task.History.Reverse<Message>())
            {
                foreach (var part in msg.Parts)
                {
                    if (part.ContentCase == PartContentCase.Data) return part.Data!.Value;
                }
            }
        }
        throw new InvalidOperationException("no data-part interrupt envelope found on task");
    }

    private static JsonElement FindFailureDataPart(AgentTask task)
    {
        var candidate = task.Status.Message;
        if (candidate is not null)
        {
            foreach (var part in candidate.Parts)
            {
                if (part.ContentCase == PartContentCase.Data) return part.Data!.Value;
            }
        }
        throw new InvalidOperationException("no failure data-part found on task.Status.Message");
    }

    private static Task<IHost> BuildHost(Func<IAgentLifecycleManager, IAgentLifecycleManager>? decorate = null) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("base-reply")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp =>
                    {
                        var baseLifecycle = new AgentLifecycleManager(
                            sp.GetRequiredService<IAgentRegistry>(),
                            sp.GetRequiredService<IAgentRuntime>());
                        return decorate is null ? baseLifecycle : decorate(baseLifecycle);
                    });
                    services.AddRouting();
                    services.AddA2AAgentServer();
                });
                web.Configure(app =>
                {
                    var lifecycle = app.ApplicationServices.GetRequiredService<IAgentLifecycleManager>();
                    lifecycle.CreateAsync(new AgentManifest(
                        "echo", "1.0",
                        new AgentHandlerRef("declarative"),
                        new[] { new ProtocolBinding("A2A") },
                        Array.Empty<ToolRef>())).GetAwaiter().GetResult();

                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapA2AAgentServer("http://localhost"));
                });
            })
            .StartAsync();

    /// <summary>Helper that replays a fixed outcome sequence across successive <see cref="InvokeAsync"/> calls.</summary>
    private sealed class ScriptedLifecycleManager : IAgentLifecycleManager
    {
        private readonly IAgentLifecycleManager _inner;
        private readonly IReadOnlyList<ScriptedOutcome> _sequence;
        private readonly Action<AgentInvocationRequest>? _observer;
        private int _index;

        public ScriptedLifecycleManager(
            IAgentLifecycleManager inner,
            IReadOnlyList<ScriptedOutcome> sequence,
            Action<AgentInvocationRequest>? observer = null)
        {
            _inner = inner;
            _sequence = sequence;
            _observer = observer;
        }

        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default) => _inner.CreateAsync(manifest, cancellationToken);

        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            _observer?.Invoke(request);
            if (_index >= _sequence.Count)
            {
                throw new InvalidOperationException($"ScriptedLifecycleManager ran out of scripted outcomes at call #{_index + 1}");
            }
            var outcome = _sequence[_index++];
            return outcome.Kind switch
            {
                ScriptedOutcomeKind.Complete => ValueTask.FromResult(new AgentInvocationResult(outcome.ReplyText!)),
                ScriptedOutcomeKind.Interrupt => throw new AgentInterruptedException(new AgentInterrupt(
                    outcome.InterruptId!,
                    outcome.Reason!,
                    JsonDocument.Parse(outcome.PayloadJson ?? "null").RootElement)
                { RunId = outcome.RunId }),
                ScriptedOutcomeKind.PolicyDeny => throw new AgentPolicyDeniedException(outcome.PolicyOperation!.Value, outcome.Reason!),
                ScriptedOutcomeKind.BudgetExceeded => throw new AgentBudgetExceededException(outcome.BudgetField!, limit: 0, observed: 0),
                _ => throw new InvalidOperationException($"unknown scripted outcome {outcome.Kind}"),
            };
        }

        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default) => _inner.SignalAsync(handle, signal, cancellationToken);
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default) => _inner.QueryAsync(handle, cancellationToken);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default) => _inner.CancelAsync(handle, cancellationToken);
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default) => _inner.UpdateAsync(handle, newManifest, cancellationToken);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default) => _inner.EvictAsync(handle, cancellationToken);
    }

    private enum ScriptedOutcomeKind { Complete, Interrupt, PolicyDeny, BudgetExceeded }

    private sealed record ScriptedOutcome(
        ScriptedOutcomeKind Kind,
        string? ReplyText = null,
        string? InterruptId = null,
        string? Reason = null,
        string? RunId = null,
        string? PayloadJson = null,
        PolicyOperation? PolicyOperation = null,
        string? BudgetField = null)
    {
        public static ScriptedOutcome Complete(string reply) => new(ScriptedOutcomeKind.Complete, ReplyText: reply);
        public static ScriptedOutcome Interrupt(string id, string reason, string payload, string? runId = null) =>
            new(ScriptedOutcomeKind.Interrupt, InterruptId: id, Reason: reason, PayloadJson: payload, RunId: runId);
        public static ScriptedOutcome PolicyDeny(PolicyOperation op, string reason) =>
            new(ScriptedOutcomeKind.PolicyDeny, PolicyOperation: op, Reason: reason);
        public static ScriptedOutcome BudgetExceeded(string field) =>
            new(ScriptedOutcomeKind.BudgetExceeded, BudgetField: field);
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
