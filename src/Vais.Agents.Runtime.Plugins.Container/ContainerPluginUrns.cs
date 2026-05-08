// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

internal static class ContainerPluginUrns
{
    public const string StartupTimeout = "urn:vais:container:startup-timeout";
    public const string AbiFailed = "urn:vais:container:abi-failed";
    public const string InvokeNetworkError = "urn:vais:container:invoke-network-error";
    public const string InvokeFailed = "urn:vais:container:invoke-failed";
    public const string HealthCheckFailed = "urn:vais:container:health-check-failed";
    public const string OpaqueStateDeserializationError = "urn:vais:container:opaque-state-deserialization-error";
    public const string SystemPromptResolutionFailed = "urn:vais:container:system-prompt-resolution-failed";
    public const string NoSupervisor = "urn:vais:container:no-supervisor";
    public const string HandlerTypeNameChanged = "urn:vais:container:handler-type-name-changed";
    public const string StartFailed = "urn:vais:container:start-failed";
}
