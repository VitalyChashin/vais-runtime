// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Plugins.Container.StructuredLog;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests.StructuredLog;

public sealed class StructuredLogEndpointTests : IAsyncDisposable
{
    private const string ValidSecret = "A32CharacterSecretKeyForTestingXX";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly HmacCallTokenService _tokenService;

    public StructuredLogEndpointTests()
    {
        var config = Substitute.For<IConfiguration>();
        config["Vais:ContainerPlugin:CallTokenSecret"].Returns(ValidSecret);
        _tokenService = new HmacCallTokenService(config);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ICallTokenService>(_tokenService);
        builder.Services.AddRouting();
        builder.Services.AddLogging();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapPluginStructuredLogEndpoints();
        _app.StartAsync().GetAwaiter().GetResult();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_ValidTokenAndJson_Returns200()
    {
        var token = _tokenService.Generate("plugin-x", "plugin-x", 60);
        var body = JsonBody("""{"severity":"INFO","message":"hello from plugin"}""");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = body;

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_MissingAuthHeader_Returns401()
    {
        var body = JsonBody("""{"severity":"INFO","message":"hello"}""");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        request.Content = body;

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_InvalidToken_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        request.Headers.Add("Authorization", "vais-plugin-token bad-token");
        request.Content = JsonBody("""{"message":"x"}""");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WrongContentType_Returns415()
    {
        var token = _tokenService.Generate("plugin-x", "plugin-x", 60);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = new StringContent("raw text", Encoding.UTF8, "text/plain");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Post_MalformedJson_Returns400()
    {
        var token = _tokenService.Generate("plugin-x", "plugin-x", 60);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = new StringContent("not-json", Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_WithExtensionDiscriminator_Returns200()
    {
        var token = _tokenService.Generate("vais-ext-log", "vais-ext-log", 60);
        var body = JsonBody("""{"severity":"DEBUG","message":"handler pre called","fields":{"seam":"agent_input"}}""");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs?source=extension&id=vais-ext-log");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = body;

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_WithAllFields_Returns200()
    {
        var token = _tokenService.Generate("my-plugin", "my-plugin", 60);
        var body = JsonBody("""
            {
              "timestamp": "2026-05-20T12:00:00Z",
              "severity": "WARN",
              "message": "slow query detected",
              "fields": { "duration_ms": 1500, "query": "SELECT *" }
            }
            """);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = body;

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
