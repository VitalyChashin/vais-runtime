// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Controls how the subprocess supervisor restarts a Python plugin process on crash.
/// Maps to <c>spec.health.restartPolicy</c> in <c>plugin.yaml</c>.
/// </summary>
public enum PythonRestartPolicy
{
    /// <summary>
    /// The subprocess is not restarted after a crash. Tool calls made while the
    /// process is down fail immediately with
    /// <see cref="PythonPluginUrns.Unavailable"/>.
    /// </summary>
    Never = 0,

    /// <summary>
    /// The subprocess is restarted with exponential back-off on crash. Tool calls
    /// made while the process is restarting block until the back-off budget is
    /// exhausted, after which they fail with <see cref="PythonPluginUrns.Unavailable"/>.
    /// Maps to the <c>exponentialBackoff</c> value in <c>plugin.yaml</c>.
    /// </summary>
    ExponentialBackoff = 1,
}

/// <summary>
/// Current ABI version for Python plugins. Bumped independently of
/// <c>VaisRuntimeAbi.CurrentVersion</c> (the .NET plugin ABI) because the two
/// contracts evolve on different axes — Python plugins speak MCP tool protocol;
/// .NET plugins implement <c>IAgentHandlerFactory</c>.
/// </summary>
public static class PythonPluginAbi
{
    /// <summary>ABI version that Python plugins must declare in <c>[tool.vais.plugin].targetApiVersion</c>.</summary>
    public const string CurrentVersion = "0.24";
}
