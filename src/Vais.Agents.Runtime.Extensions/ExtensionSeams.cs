// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>Canonical seam names used in extension manifests and handler bindings.</summary>
internal static class ExtensionSeams
{
    public const string AgentInput  = "agentInput";
    public const string AgentOutput = "agentOutput";
    public const string ToolGatewayMiddleware = "toolGatewayMiddleware";
    public const string LlmGatewayMiddleware = "llmGatewayMiddleware";
    public const string ErrorInterceptor = "errorInterceptor";
}
