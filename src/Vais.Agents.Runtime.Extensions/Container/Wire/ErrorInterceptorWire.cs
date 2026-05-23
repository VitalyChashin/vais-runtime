// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for the <c>errorInterceptor</c> seam. Unlike the other seams this is a
/// single call (no pre/post pair): the runtime POSTs the failure to the handler's advertised
/// endpoint and reads back an optional replacement message. Field names serialize camelCase.
/// </summary>
internal sealed record ErrorInterceptorRequest(
    string CallId,
    ErrorContextWire Context);

internal sealed record ErrorContextWire(
    string AgentId,
    string? RunId,
    string? NodeId,
    string ErrorType,
    string ErrorMessage);

/// <summary>
/// <c>errorInterceptor</c> response. A non-empty <see cref="Message"/> replaces the user-facing
/// error message; null/empty leaves it unchanged (observe-only). The handler can never change the
/// error type or suppress the failure (P9).
/// </summary>
internal sealed record ErrorInterceptorResponse(
    string? Message);
