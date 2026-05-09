// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

internal interface IContainerSupervisor : IAsyncDisposable
{
    ContainerPluginDescriptor Descriptor { get; }
    ContainerPluginStatus Status { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<ContainerReplaceResult> DrainAndReplaceAsync(string? newImage, CancellationToken ct);
    bool TryAcquireInvoke();
    void ReleaseInvoke();
}
