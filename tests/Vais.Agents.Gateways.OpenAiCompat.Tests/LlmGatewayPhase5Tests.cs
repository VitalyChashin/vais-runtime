// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Core;
using Vais.Agents.Gateways.OpenAiCompat.Models;
using Xunit;

namespace Vais.Agents.Gateways.OpenAiCompat.Tests;

/// <summary>
/// GW-25 — OpenAI-compatible gateway endpoint and translator tests.
/// </summary>
public sealed class LlmGatewayPhase5Tests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;

    // ── IAsyncLifetime ───────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var provider = new FakeProvider(
            _ => new CompletionResponse("hello world", "test-model", 10, 5));

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddOpenAiCompatGateway();
                    services.AddPassThroughIdentityResolver();
                    services.AddInMemoryModelRouter(routes =>
                        routes.Add("test-model", new ModelRoute(provider, new ModelSpec("test", "test-model"))));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapOpenAiCompat());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── GW-25: endpoint tests ────────────────────────────────────────────────

    [Fact]
    public async Task NonStreaming_Returns_200_With_Valid_Json_Shape()
    {
        var body = new { model = "test-model", messages = new[] { new { role = "user", content = "hi" } } };

        var response = await _http.PostAsJsonAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("object").GetString().Should().Be("chat.completion");
        json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            .Should().Be("hello world");
        json.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        json.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(10);
        json.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Unknown_Model_Returns_404_With_Error_Body()
    {
        var body = new { model = "nonexistent-llm", messages = new[] { new { role = "user", content = "hi" } } };

        var response = await _http.PostAsJsonAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("error").GetProperty("type").GetString().Should().Be("model_not_found");
    }

    [Fact]
    public async Task Rejecting_Identity_Resolver_Returns_401()
    {
        // Spin up a separate host with a rejecting identity resolver
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddOpenAiCompatGateway();
                    services.AddSingleton<IInboundIdentityResolver>(new RejectingIdentityResolver());
                    services.AddInMemoryModelRouter(routes =>
                        routes.Add("m", new ModelRoute(
                            new FakeProvider(_ => new CompletionResponse("ok")),
                            new ModelSpec("t", "m"))));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapOpenAiCompat());
                });
            })
            .StartAsync();

        var http = host.GetTestClient();

        try
        {
            var body = new { model = "m", messages = new[] { new { role = "user", content = "hi" } } };
            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("type").GetString().Should().Be("invalid_api_key");
        }
        finally
        {
            http.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Streaming_Returns_EventStream_With_Data_Lines_And_Done()
    {
        // Build separate host with streaming provider
        var streamingProvider = new FakeStreamingProvider(
        [
            new CompletionUpdate("Hello"),
            new CompletionUpdate(" world"),
            new CompletionUpdate("!", "test-model", 8, 3)
        ]);

        var host = await BuildStreamingHostAsync(streamingProvider);
        var http = host.GetTestClient();

        try
        {
            var body = new { model = "stream-model", stream = true, messages = new[] { new { role = "user", content = "hi" } } };
            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            var lines = await ReadSseDataLinesAsync(response);

            // 3 content-bearing data lines + 1 finish chunk + [DONE]
            var dataLines = lines.Where(l => l != "[DONE]").ToList();
            dataLines.Should().HaveCount(4); // 3 content + 1 finish_reason chunk
            lines.Should().Contain("[DONE]");

            // All data lines parse as valid chunks
            foreach (var line in dataLines)
            {
                var chunk = JsonSerializer.Deserialize<JsonElement>(line);
                chunk.GetProperty("object").GetString().Should().Be("chat.completion.chunk");
            }
        }
        finally
        {
            http.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Streaming_ToolCall_Delta_Has_Correct_Structure()
    {
        var toolCallArg = JsonDocument.Parse(@"{""q"":""weather""}").RootElement;
        var streamingProvider = new FakeStreamingProvider(
        [
            new CompletionUpdate("",
                ToolCalls: new ToolCallRequest[] { new("get_weather", toolCallArg, "call-abc") })
        ]);

        var host = await BuildStreamingHostAsync(streamingProvider);
        var http = host.GetTestClient();

        try
        {
            var body = new { model = "stream-model", stream = true, messages = new[] { new { role = "user", content = "hi" } } };
            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            var lines = await ReadSseDataLinesAsync(response);
            var dataLines = lines.Where(l => l != "[DONE]").ToList();

            // The tool-call chunk
            var toolChunkLine = dataLines.First(l =>
            {
                var el = JsonSerializer.Deserialize<JsonElement>(l);
                return el.GetProperty("choices")[0].GetProperty("delta")
                    .TryGetProperty("tool_calls", out _);
            });

            var chunk = JsonSerializer.Deserialize<JsonElement>(toolChunkLine);
            var toolCall = chunk.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
            toolCall.GetProperty("id").GetString().Should().Be("call-abc");
            toolCall.GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        }
        finally
        {
            http.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task BudgetExceeded_Returns_429()
    {
        var throwingProvider = new FakeProvider(
            _ => throw new AgentBudgetExceededException("MaxTokens", 100, 200));

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddOpenAiCompatGateway();
                    services.AddPassThroughIdentityResolver();
                    services.AddInMemoryModelRouter(routes =>
                        routes.Add("throw-model", new ModelRoute(throwingProvider, new ModelSpec("t", "throw-model"))));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapOpenAiCompat());
                });
            })
            .StartAsync();

        var http = host.GetTestClient();

        try
        {
            var body = new { model = "throw-model", messages = new[] { new { role = "user", content = "hi" } } };
            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("type").GetString().Should().Be("rate_limit_exceeded");
        }
        finally
        {
            http.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Get_Models_Returns_Configured_Aliases()
    {
        var response = await _http.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("object").GetString().Should().Be("list");

        var ids = json.GetProperty("data").EnumerateArray()
            .Select(el => el.GetProperty("id").GetString())
            .ToArray();

        ids.Should().Contain("test-model");
    }

    // ── GW-25: translator unit tests ─────────────────────────────────────────

    [Fact]
    public void Translator_ToCompletionRequest_Extracts_System_Message()
    {
        var req = new ChatCompletionRequest
        {
            Model = "m",
            Messages =
            [
                new ChatMessage { Role = "system", Content = "Be helpful." },
                new ChatMessage { Role = "user", Content = "What is 2+2?" }
            ]
        };

        var result = OpenAiTranslator.ToCompletionRequest(req);

        result.SystemPrompt.Should().Be("Be helpful.");
        result.History.Should().HaveCount(1);
        result.History[0].Role.Should().Be(AgentChatRole.User);
        result.History[0].Text.Should().Be("What is 2+2?");
    }

    [Fact]
    public void Translator_ToChatCompletionResponse_Maps_Usage_And_ToolCalls()
    {
        var tcArgs = JsonDocument.Parse(@"{""x"":1}").RootElement;
        var response = new CompletionResponse(
            "",
            "gpt-4o",
            PromptTokens: 20,
            CompletionTokens: 15,
            ToolCalls: [new ToolCallRequest("my_tool", tcArgs, "call-1")]);

        var result = OpenAiTranslator.ToChatCompletionResponse(response, "gpt-4o", "chatcmpl-test");

        result.Usage!.PromptTokens.Should().Be(20);
        result.Usage.CompletionTokens.Should().Be(15);
        result.Usage.TotalTokens.Should().Be(35);
        result.Choices[0].FinishReason.Should().Be("tool_calls");
        result.Choices[0].Message.ToolCalls.Should().HaveCount(1);
        result.Choices[0].Message.ToolCalls![0].Id.Should().Be("call-1");
        result.Choices[0].Message.ToolCalls![0].Function.Name.Should().Be("my_tool");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<IHost> BuildStreamingHostAsync(FakeStreamingProvider provider)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddOpenAiCompatGateway();
                    services.AddPassThroughIdentityResolver();
                    services.AddInMemoryModelRouter(routes =>
                        routes.Add("stream-model", new ModelRoute(provider, new ModelSpec("t", "stream-model"))));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapOpenAiCompat());
                });
            })
            .StartAsync();
    }

    private static async Task<List<string>> ReadSseDataLinesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var dataLines = new List<string>();

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                dataLines.Add(line["data: ".Length..]);
        }

        return dataLines;
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class FakeProvider : ICompletionProvider
    {
        private readonly Func<CompletionRequest, CompletionResponse> _respond;

        internal FakeProvider(Func<CompletionRequest, CompletionResponse> respond) => _respond = respond;

        public string ProviderName => "Fake";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_respond(request));
    }

    private sealed class FakeStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly IReadOnlyList<CompletionUpdate> _updates;

        internal FakeStreamingProvider(IReadOnlyList<CompletionUpdate> updates) => _updates = updates;

        public string ProviderName => "FakeStreaming";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse(string.Concat(_updates.Select(u => u.TextDelta))));

#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
            CompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in _updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
#pragma warning restore CS1998
    }

    private sealed class RejectingIdentityResolver : IInboundIdentityResolver
    {
        public ValueTask<AgentContext> ResolveAsync(string bearerToken, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("Invalid API key.");
    }
}
