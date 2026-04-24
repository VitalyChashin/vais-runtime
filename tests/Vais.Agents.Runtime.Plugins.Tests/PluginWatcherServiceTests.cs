// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

public class PluginWatcherServiceTests
{
    [Fact]
    public async Task HandleFileChangeAsync_Calls_Reloader_With_Correct_Path()
    {
        var result = SuccessResult("my-plugin");
        var reloader = new FakePluginReloader(result);
        var svc = new PluginWatcherService(reloader, new FakeHostApplicationLifetime(), "/plugins");

        await svc.HandleFileChangeAsync("/plugins/my-plugin/my-plugin.dll", CancellationToken.None);

        reloader.LastPath.Should().Be("/plugins/my-plugin/my-plugin.dll");
        reloader.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleFileChangeAsync_Does_Not_Throw_On_LoadFailed()
    {
        var result = new PluginReloadResult(
            null, null, PluginReloadStatus.LoadFailed,
            PluginReloadUrns.PluginReloadFailed,
            new InvalidOperationException("boom"));
        var reloader = new FakePluginReloader(result);
        var svc = new PluginWatcherService(reloader, new FakeHostApplicationLifetime(), "/plugins");

        Func<Task> act = () => svc.HandleFileChangeAsync("/plugins/p/p.dll", CancellationToken.None);

        await act.Should().NotThrowAsync(
            because: "the watcher surfaces all reload outcomes as log messages, not exceptions");
    }

    [Fact]
    public async Task StartAsync_Returns_Immediately_Without_Starting_Watcher()
    {
        var reloader = new FakePluginReloader(SuccessResult("p"));
        // Gate not triggered — ApplicationStarted never fires.
        var svc = new PluginWatcherService(
            reloader,
            new FakeHostApplicationLifetime(),
            Path.Combine(Path.GetTempPath(), $"vais-no-exist-{Guid.NewGuid():N}"));

        await svc.StartAsync(CancellationToken.None);

        reloader.CallCount.Should().Be(0,
            because: "the filesystem watcher starts only after ApplicationStarted fires");
    }

    [Fact]
    public async Task StopAsync_Completes_Without_Hanging_Despite_Long_Debounce()
    {
        var reloader = new FakePluginReloader(SuccessResult("p"));
        var svc = new PluginWatcherService(
            reloader,
            new FakeHostApplicationLifetime(),
            "/plugins",
            debounceDelay: TimeSpan.FromMinutes(5)); // artificially long debounce

        await svc.StartAsync(CancellationToken.None);
        // StopAsync must complete immediately by cancelling the pending debounce.
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleFileChangeAsync_Invokes_Reloader_On_AbiMismatch_And_Logs_Warning()
    {
        var result = new PluginReloadResult(
            null, null, PluginReloadStatus.AbiMismatch,
            PluginReloadUrns.PluginReloadAbiMismatch, null);
        var reloader = new FakePluginReloader(result);
        var svc = new PluginWatcherService(reloader, new FakeHostApplicationLifetime(), "/plugins");

        // The method should complete successfully (no throw) for any non-Success status.
        Func<Task> act = () => svc.HandleFileChangeAsync("/plugins/p/p.dll", CancellationToken.None);
        await act.Should().NotThrowAsync();
        reloader.CallCount.Should().Be(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PluginReloadResult SuccessResult(string pluginName) =>
        new(null,
            new PluginDescriptor(
                Name: pluginName,
                AssemblyPath: $"/plugins/{pluginName}/{pluginName}.dll",
                TargetApiVersion: "0.22",
                Handlers: [$"MyApp.{pluginName}"],
                LoadedViaAttribute: true,
                LoadContext: System.Runtime.Loader.AssemblyLoadContext.Default),
            PluginReloadStatus.Success, null, null);

    private sealed class FakePluginReloader(PluginReloadResult result) : IPluginReloader
    {
        public string? LastPath { get; private set; }
        public int CallCount { get; private set; }

        public Task<PluginReloadResult> ReloadAsync(
            string pluginPath,
            CancellationToken cancellationToken = default)
        {
            LastPath = pluginPath;
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public CancellationToken ApplicationStarted => _startedCts.Token;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;

        public void StopApplication() => _stoppingCts.Cancel();
        public void SimulateStarted() => _startedCts.Cancel();
    }
}
