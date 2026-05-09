// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed record KubernetesPluginConfig(
    string ServiceUrl,
    string DeploymentName,
    string Namespace);
