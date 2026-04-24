// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

public class PluginAssemblyLoadContextTests
{
    [Fact]
    public void SharedAssemblies_List_Includes_Every_DI_Boundary_Type()
    {
        // Locked contract — the findings doc committed to this exact list. Additions
        // require a findings-doc amendment + this test update.
        var shared = PluginAssemblyLoadContext.SharedAssembliesForTesting;

        shared.Should().Contain(new[]
        {
            // Vais.Agents ABI
            "Vais.Agents.Abstractions",
            "Vais.Agents.Core",
            "Vais.Agents.Control.Abstractions",
            // DI + hosting + logging + options + configuration abstractions
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Configuration.Abstractions",
            // MEAI
            "Microsoft.Extensions.AI",
            "Microsoft.Extensions.AI.Abstractions",
            // Polly
            "Polly.Core",
        });
    }

    [Fact]
    public void Vais_Agents_Abstractions_Is_Treated_As_Shared()
    {
        // Resolving a shared assembly via a plugin load context must defer to Default
        // so plugin types and runtime types see the same IAiAgent / IAgentHandlerFactory.
        var ctx = new PluginAssemblyLoadContext(typeof(IAiAgent).Assembly.Location);

        var assembly = ctx.LoadFromAssemblyName(new AssemblyName("Vais.Agents.Abstractions"));

        assembly.Should().NotBeNull();
        // The shared carve-out returns null from Load(), which makes the base
        // context delegate to Default — so the resolved assembly is the one
        // already in Default (our test-runner's).
        assembly.Should().BeSameAs(typeof(IAiAgent).Assembly);
    }

    [Fact]
    public void Empty_Path_Ctor_Throws()
    {
        Action act = () => new PluginAssemblyLoadContext("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Collectible_Alc_Is_Garbage_Collected_After_Unload()
    {
        // Non-generic WeakReference avoids any typed reference the JIT might
        // keep alive in a register across GC boundaries.
        var wr = CreateContextAndUnload();

        for (var i = 0; i < 10; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
        }

        wr.IsAlive.Should().BeFalse(
            "a collectible PluginAssemblyLoadContext must be GC-eligible once Unload() is called and all references are released");
    }

    // NoInlining prevents the JIT from keeping `ctx` alive in a register
    // in the calling method's stack frame, which would defeat the weak-reference check.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateContextAndUnload()
    {
        var ctx = new PluginAssemblyLoadContext(typeof(PluginAssemblyLoadContext).Assembly.Location);
        var wr = new WeakReference(ctx);
        ctx.Unload();
        return wr;
    }
}
