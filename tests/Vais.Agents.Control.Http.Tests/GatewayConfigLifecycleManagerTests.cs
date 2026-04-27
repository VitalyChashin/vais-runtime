// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// GCF-19 Phase 2, scenarios 1-4: in-process lifecycle manager CRUD for LLM gateway configs,
/// exercised directly without HTTP.
/// </summary>
public sealed class GatewayConfigLifecycleManagerTests
{
    private static LlmGatewayConfigManifest MinimalConfig(string id = "test-gw", string version = "1.0") =>
        new(Id: id, Version: version, Middleware: Array.Empty<GatewayMiddlewareSpec>());

    [Fact]
    public async Task Create_ValidManifest_ReturnsHandle()
    {
        var registry = new InMemoryLlmGatewayConfigRegistry();
        var manager = new LlmGatewayConfigLifecycleManager(registry);

        var handle = await manager.CreateAsync(MinimalConfig());

        handle.Id.Should().Be("test-gw");
        handle.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Create_Duplicate_ThrowsConflict()
    {
        var registry = new InMemoryLlmGatewayConfigRegistry();
        var manager = new LlmGatewayConfigLifecycleManager(registry);
        await manager.CreateAsync(MinimalConfig());

        await manager.Invoking(m => m.CreateAsync(MinimalConfig()).AsTask())
            .Should().ThrowAsync<LlmGatewayConfigConflictException>();
    }

    [Fact]
    public async Task Query_UnknownHandle_ThrowsNotFound()
    {
        var registry = new InMemoryLlmGatewayConfigRegistry();
        var manager = new LlmGatewayConfigLifecycleManager(registry);

        await manager.Invoking(m => m.QueryAsync(new LlmGatewayConfigHandle("missing", "1.0")).AsTask())
            .Should().ThrowAsync<LlmGatewayConfigHandleNotFoundException>();
    }

    [Fact]
    public async Task Evict_AfterCreate_Succeeds_SecondEvict_ThrowsNotFound()
    {
        var registry = new InMemoryLlmGatewayConfigRegistry();
        var manager = new LlmGatewayConfigLifecycleManager(registry);
        await manager.CreateAsync(MinimalConfig());
        var handle = new LlmGatewayConfigHandle("test-gw", "1.0");

        await manager.EvictAsync(handle);

        await manager.Invoking(m => m.EvictAsync(handle).AsTask())
            .Should().ThrowAsync<LlmGatewayConfigHandleNotFoundException>();
    }
}
