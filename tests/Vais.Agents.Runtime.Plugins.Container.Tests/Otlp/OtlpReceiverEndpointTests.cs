// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Plugins.Container.Otlp;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests.Otlp;

public sealed class OtlpReceiverEndpointTests : IAsyncDisposable
{
    private const string ValidSecret = "A32CharacterSecretKeyForTestingXX";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly HmacCallTokenService _tokenService;

    public OtlpReceiverEndpointTests()
    {
        var config = Substitute.For<IConfiguration>();
        config["Vais:ContainerPlugin:CallTokenSecret"].Returns(ValidSecret);
        _tokenService = new HmacCallTokenService(config);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ICallTokenService>(_tokenService);
        builder.Services.AddRouting();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapPluginOtlpEndpoints();
        _app.StartAsync().GetAwaiter().GetResult();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] BuildMinimalExportRequest()
    {
        // Build a minimal valid OTLP ExportTraceServiceRequest protobuf binary:
        // ExportTraceServiceRequest { resource_spans: [ ResourceSpans { scope_spans: [ ScopeSpans { spans: [ Span { ... } ] } ] } ] }
        var traceId = new byte[16]; traceId[0] = 0x11;
        var spanId  = new byte[8];  spanId[0]  = 0x22;

        using var spanMs = new MemoryStream();
        using (var spanOut = new CodedOutputStream(spanMs, leaveOpen: true))
        {
            // field 1 = trace_id (bytes)
            spanOut.WriteTag(1, WireFormat.WireType.LengthDelimited);
            spanOut.WriteBytes(ByteString.CopyFrom(traceId));
            // field 2 = span_id (bytes)
            spanOut.WriteTag(2, WireFormat.WireType.LengthDelimited);
            spanOut.WriteBytes(ByteString.CopyFrom(spanId));
            // field 5 = name (string)
            spanOut.WriteTag(5, WireFormat.WireType.LengthDelimited);
            spanOut.WriteString("test-span");
            // field 7 = start_time_unix_nano (fixed64)
            spanOut.WriteTag(7, WireFormat.WireType.Fixed64);
            spanOut.WriteFixed64(1_000_000_000UL);
            // field 8 = end_time_unix_nano (fixed64)
            spanOut.WriteTag(8, WireFormat.WireType.Fixed64);
            spanOut.WriteFixed64(2_000_000_000UL);
        }
        var spanBytes = ByteString.CopyFrom(spanMs.ToArray());

        using var scopeMs = new MemoryStream();
        using (var scopeOut = new CodedOutputStream(scopeMs, leaveOpen: true))
        {
            // field 2 = spans (repeated Span)
            scopeOut.WriteTag(2, WireFormat.WireType.LengthDelimited);
            scopeOut.WriteBytes(spanBytes);
        }
        var scopeBytes = ByteString.CopyFrom(scopeMs.ToArray());

        using var rsMs = new MemoryStream();
        using (var rsOut = new CodedOutputStream(rsMs, leaveOpen: true))
        {
            // field 2 = scope_spans (repeated ScopeSpans)
            rsOut.WriteTag(2, WireFormat.WireType.LengthDelimited);
            rsOut.WriteBytes(scopeBytes);
        }
        var rsBytes = ByteString.CopyFrom(rsMs.ToArray());

        using var reqMs = new MemoryStream();
        using (var reqOut = new CodedOutputStream(reqMs, leaveOpen: true))
        {
            // field 1 = resource_spans (repeated ResourceSpans)
            reqOut.WriteTag(1, WireFormat.WireType.LengthDelimited);
            reqOut.WriteBytes(rsBytes);
        }
        return reqMs.ToArray();
    }

    [Fact]
    public async Task Post_ValidTokenAndProtobuf_Returns200()
    {
        var token = _tokenService.Generate("plugin-x", "plugin-x", 60);
        var body = BuildMinimalExportRequest();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/otlp/v1/traces");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_MissingAuthHeader_Returns401()
    {
        var body = BuildMinimalExportRequest();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/otlp/v1/traces");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_InvalidToken_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/otlp/v1/traces");
        request.Headers.Add("Authorization", "vais-plugin-token invalid-token-value");
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WrongContentType_Returns415()
    {
        var token = _tokenService.Generate("plugin-x", "plugin-x", 60);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/otlp/v1/traces");
        request.Headers.Add("Authorization", $"vais-plugin-token {token}");
        request.Content = new StringContent("{}",
            System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }
}
