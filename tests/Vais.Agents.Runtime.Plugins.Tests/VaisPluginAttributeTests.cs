// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

public class VaisPluginAttributeTests
{
    [Fact]
    public void Construction_Sets_All_Fields()
    {
        var attr = new VaisPluginAttribute("0.18", "MyApp.Foo", "MyApp.Bar");

        attr.TargetApiVersion.Should().Be("0.18");
        attr.Handlers.Should().ContainInOrder("MyApp.Foo", "MyApp.Bar");
    }

    [Fact]
    public void Construction_Accepts_SingleHandler()
    {
        var attr = new VaisPluginAttribute("0.18", "MyApp.Foo");

        attr.Handlers.Should().ContainSingle().Which.Should().Be("MyApp.Foo");
    }

    [Fact]
    public void Empty_ApiVersion_Throws()
    {
        Action act = () => new VaisPluginAttribute("", "MyApp.Foo");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_Handler_Entry_Throws()
    {
        Action act = () => new VaisPluginAttribute("0.18", "MyApp.Foo", "");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*non-empty fully-qualified type names*");
    }
}
