// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Telemetry;

/// <summary>
/// D-1 verify gate: TrajectoryEvent + TrajectoryArgumentRedactor.
/// - JSON-object args produce a name → JsonValueKind shape map.
/// - Secret-shaped names (apiKey, token, password, secret, auth, credential, privateKey,
///   passphrase) are omitted entirely — neither name nor value persists.
/// - Non-object inputs (string, null, array) yield an empty shape.
/// - WithAdditionalSecretNameSubstrings extends the deny-list without dropping defaults.
/// </summary>
public sealed class TrajectoryEventTests
{
    [Fact]
    public void Redactor_ProducesNameToTypeMapForObjectArguments()
    {
        var args = JsonDocument.Parse(
            """{"url":"https://example.com","limit":10,"flag":true,"nested":{"x":1},"items":[]}""").RootElement;

        var shape = TrajectoryArgumentRedactor.Default.ToShape(args);

        shape.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["url"] = "string",
            ["limit"] = "number",
            ["flag"] = "true",
            ["nested"] = "object",
            ["items"] = "array",
        });
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("API_KEY")]
    [InlineData("ApiKeyForX")]
    [InlineData("token")]
    [InlineData("bearerToken")]
    [InlineData("password")]
    [InlineData("PASSWORD")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("authorization")]
    [InlineData("authToken")]
    [InlineData("credential")]
    [InlineData("aws_credentials")]
    [InlineData("privateKey")]
    [InlineData("private_key")]
    [InlineData("passphrase")]
    public void Redactor_OmitsSecretShapedNames(string secretArgName)
    {
        var args = JsonDocument.Parse($"{{\"{secretArgName}\":\"sk-12345\",\"keep\":\"ok\"}}").RootElement;

        var shape = TrajectoryArgumentRedactor.Default.ToShape(args);

        shape.Should().NotContainKey(secretArgName,
            $"'{secretArgName}' matches the secret deny-list; raw value must never appear in the shape");
        shape.Should().ContainKey("keep");
    }

    [Fact]
    public void Redactor_NonObjectArgumentsYieldsEmptyShape()
    {
        var strArg = JsonDocument.Parse("\"a literal string\"").RootElement;
        var arrayArg = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var nullArg = JsonDocument.Parse("null").RootElement;

        TrajectoryArgumentRedactor.Default.ToShape(strArg).Should().BeEmpty();
        TrajectoryArgumentRedactor.Default.ToShape(arrayArg).Should().BeEmpty();
        TrajectoryArgumentRedactor.Default.ToShape(nullArg).Should().BeEmpty();
    }

    [Fact]
    public void Redactor_WithAdditionalSubstrings_ExtendsDenyList_WithoutDroppingDefaults()
    {
        var extended = TrajectoryArgumentRedactor.WithAdditionalSecretNameSubstrings("customsecret", "internal_id");
        var args = JsonDocument.Parse("""{"customsecretFoo":"x","internal_idBar":"y","apiKey":"z","plain":"ok"}""").RootElement;

        var shape = extended.ToShape(args);

        shape.Keys.Should().BeEquivalentTo(["plain"],
            "default deny-list (apiKey) AND extensions (customsecret, internal_id) both apply");
    }

    [Fact]
    public void TrajectoryEvent_RoundTripsThroughSystemTextJson()
    {
        var original = new TrajectoryEvent
        {
            EventId = "abc123",
            Timestamp = DateTimeOffset.Parse("2026-05-26T12:00:00Z"),
            EventName = "tool.call",
            Operation = OntologyOperation.Call,
            AgentId = "coord",
            RunId = "run-1",
            ConceptName = "fetch_url",
            Transport = "south",
            ArgumentsShape = new Dictionary<string, string> { ["url"] = "string" },
            Outcome = new TrajectoryOutcome(TrajectoryOutcomeKind.Ok),
            OntologyVersion = "v1",
            Duration = TimeSpan.FromMilliseconds(150),
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<TrajectoryEvent>(json);

        deserialized.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void TrajectoryOutcome_CarriesErrorTypeOnNonOkKind()
    {
        var ok = new TrajectoryOutcome(TrajectoryOutcomeKind.Ok);
        var err = new TrajectoryOutcome(TrajectoryOutcomeKind.Error, "HttpRequestException");
        var sc = new TrajectoryOutcome(TrajectoryOutcomeKind.ShortCircuit, "delegation-denied");

        ok.ErrorType.Should().BeNull();
        err.ErrorType.Should().Be("HttpRequestException");
        sc.ErrorType.Should().Be("delegation-denied");
    }
}
