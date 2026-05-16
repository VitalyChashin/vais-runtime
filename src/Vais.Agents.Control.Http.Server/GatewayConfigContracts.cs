// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>Response body for <c>POST /v1/llm-gateways</c> (create) and <c>PATCH /v1/llm-gateways/{id}</c> (update).</summary>
public sealed record LlmGatewayConfigApplyResponse(
    LlmGatewayConfigHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Response body for <c>GET /v1/llm-gateways/{id}</c>.</summary>
public sealed record LlmGatewayConfigQueryResponse(
    LlmGatewayConfigManifest Manifest,
    LlmGatewayConfigHandle Handle,
    LlmGatewayConfigStatus Status);

/// <summary>Response body for <c>GET /v1/llm-gateways</c>.</summary>
public sealed record LlmGatewayConfigListResponse(
    IReadOnlyList<LlmGatewayConfigManifest> Items,
    string? NextCursor = null);

/// <summary>Response body for <c>POST /v1/llm-gateways/validate</c>. Inspect <see cref="Valid"/> to drive exit-code decisions.</summary>
public sealed record LlmGatewayConfigValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>Response body for <c>POST /v1/mcp-gateways</c> (create) and <c>PATCH /v1/mcp-gateways/{id}</c> (update).</summary>
public sealed record McpGatewayConfigApplyResponse(
    McpGatewayConfigHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Response body for <c>GET /v1/mcp-gateways/{id}</c>.</summary>
public sealed record McpGatewayConfigQueryResponse(
    McpGatewayConfigManifest Manifest,
    McpGatewayConfigHandle Handle,
    McpGatewayConfigStatus Status);

/// <summary>Response body for <c>GET /v1/mcp-gateways</c>.</summary>
public sealed record McpGatewayConfigListResponse(
    IReadOnlyList<McpGatewayConfigManifest> Items,
    string? NextCursor = null);

/// <summary>Response body for <c>POST /v1/mcp-gateways/validate</c>.</summary>
public sealed record McpGatewayConfigValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>Response body for <c>POST /v1/mcp-servers</c> (create) and <c>PATCH /v1/mcp-servers/{id}</c> (update).</summary>
public sealed record McpServerApplyResponse(
    McpServerHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Response body for <c>GET /v1/mcp-servers/{id}</c>.</summary>
public sealed record McpServerQueryResponse(
    McpServerManifest Manifest,
    McpServerHandle Handle,
    McpServerStatus Status);

/// <summary>Response body for <c>GET /v1/mcp-servers</c>.</summary>
public sealed record McpServerListResponse(
    IReadOnlyList<McpServerManifest> Items,
    string? NextCursor = null);

/// <summary>Response body for <c>POST /v1/mcp-servers/validate</c>. Includes cross-ref checks against registered sources and gateway.</summary>
public sealed record McpServerValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>Response body for <c>POST /v1/container-plugins</c> (create) and <c>PATCH /v1/container-plugins/{id}</c> (update).</summary>
public sealed record ContainerPluginApplyResponse(
    ContainerPluginHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Response body for <c>GET /v1/container-plugins/{id}</c>.</summary>
public sealed record ContainerPluginQueryResponse(
    ContainerPluginManifest Manifest,
    ContainerPluginHandle Handle,
    ContainerPluginRuntimeStatus Status);

/// <summary>Response body for <c>GET /v1/container-plugins</c>.</summary>
public sealed record ContainerPluginListResponse(
    IReadOnlyList<ContainerPluginManifest> Items,
    string? NextCursor = null);

/// <summary>Response body for <c>POST /v1/container-plugins/validate</c>.</summary>
public sealed record ContainerPluginValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>Response body for <c>POST /v1/eval-suites</c> (upsert).</summary>
public sealed record EvalSuiteApplyResponse(
    EvalSuiteHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Response body for <c>GET /v1/eval-suites/{id}</c>.</summary>
public sealed record EvalSuiteQueryResponse(
    EvalSuiteManifest Manifest,
    EvalSuiteHandle Handle);

/// <summary>Response body for <c>GET /v1/eval-suites</c>.</summary>
public sealed record EvalSuiteListResponse(
    IReadOnlyList<EvalSuiteManifest> Items,
    string? NextCursor = null);
