// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class InvokeGraphCommandTests
{
    [Fact]
    public void ParseStateBag_Null_ReturnsEmptyDictionary()
    {
        var result = InvokeGraphCommand.ParseStateBag(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseStateBag_EmptyString_ReturnsEmptyDictionary()
    {
        var result = InvokeGraphCommand.ParseStateBag(string.Empty);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseStateBag_WhitespaceOnly_ReturnsEmptyDictionary()
    {
        var result = InvokeGraphCommand.ParseStateBag("   ");
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseStateBag_JsonObject_ReturnsKeys()
    {
        var result = InvokeGraphCommand.ParseStateBag(@"{""user_query"": ""hello"", ""count"": 3}");
        result.Should().ContainKey("user_query");
        result.Should().ContainKey("count");
        result["user_query"].GetString().Should().Be("hello");
        result["count"].GetInt32().Should().Be(3);
    }

    [Fact]
    public void ParseStateBag_JsonArray_Throws()
    {
        var act = () => InvokeGraphCommand.ParseStateBag("[1, 2, 3]");
        act.Should().Throw<JsonException>().WithMessage("*object*");
    }

    [Fact]
    public void ParseStateBag_InvalidJson_Throws()
    {
        var act = () => InvokeGraphCommand.ParseStateBag("{not valid}");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ParseStateBag_EmptyObject_ReturnsEmptyDictionary()
    {
        var result = InvokeGraphCommand.ParseStateBag("{}");
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseStateBag_NestedObject_Roundtrips()
    {
        var json = @"{""nested"": {""a"": 1}}";
        var result = InvokeGraphCommand.ParseStateBag(json);
        result.Should().ContainKey("nested");
        result["nested"].ValueKind.Should().Be(JsonValueKind.Object);
    }
}
