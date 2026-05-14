// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

internal enum PythonPluginLoadStatus
{
    Success = 0,
    AlreadyLoaded = 1,
    HandshakeFailed = 2,
    SecretResolutionFailed = 3,
}

internal readonly record struct PythonPluginLoadResult(
    string PluginName,
    PythonPluginLoadStatus Status,
    string? ErrorMessage = null);
