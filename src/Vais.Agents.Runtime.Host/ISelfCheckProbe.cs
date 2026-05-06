// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Host;

internal interface ISelfCheckProbe
{
    string ServiceName { get; }
    bool IsRequired { get; }
    Task<SelfCheckResult> ProbeAsync(CancellationToken ct);
}
