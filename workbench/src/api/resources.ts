import type { VaisClient } from './client'
import type {
  AgentManifest,
  AgentGraphManifest,
  LlmGatewayConfigManifest,
  McpGatewayConfigManifest,
  McpServerManifest,
  ListResponse,
  ValidateResult,
} from './types'

export const listAgents = (c: VaisClient) => c.get<ListResponse<AgentManifest>>('/v1/agents').then(r => r.items)
export const getAgent = (c: VaisClient, id: string) => c.get<{ manifest: AgentManifest }>(`/v1/agents/${id}`).then(r => r.manifest)
export const createAgent = (c: VaisClient, body: unknown) => c.post<AgentManifest>('/v1/agents', body)
export const validateAgent = (c: VaisClient, body: unknown) => c.post<ValidateResult>('/v1/agents/validate', body)
export const deleteAgent = (c: VaisClient, id: string) => c.delete(`/v1/agents/${id}`)

export const listGraphs = (c: VaisClient) => c.get<ListResponse<AgentGraphManifest>>('/v1/graphs').then(r => r.items)
export const getGraph = (c: VaisClient, id: string) => c.get<{ manifest: AgentGraphManifest }>(`/v1/graphs/${id}`).then(r => r.manifest)
export const createGraph = (c: VaisClient, body: unknown) => c.post<AgentGraphManifest>('/v1/graphs', body)
export const validateGraph = (c: VaisClient, body: unknown) => c.post<ValidateResult>('/v1/graphs/validate', body)
export const deleteGraph = (c: VaisClient, id: string) => c.delete(`/v1/graphs/${id}`)

export const listLlmGateways = (c: VaisClient) => c.get<ListResponse<LlmGatewayConfigManifest>>('/v1/llm-gateways').then(r => r.items)
export const getLlmGateway = (c: VaisClient, id: string) => c.get<{ manifest: LlmGatewayConfigManifest }>(`/v1/llm-gateways/${id}`).then(r => r.manifest)
export const createLlmGateway = (c: VaisClient, body: unknown) => c.post<LlmGatewayConfigManifest>('/v1/llm-gateways', body)
export const validateLlmGateway = (c: VaisClient, body: unknown) => c.post<ValidateResult>('/v1/llm-gateways/validate', body)
export const deleteLlmGateway = (c: VaisClient, id: string) => c.delete(`/v1/llm-gateways/${id}`)

export const listMcpGateways = (c: VaisClient) => c.get<ListResponse<McpGatewayConfigManifest>>('/v1/mcp-gateways').then(r => r.items)
export const getMcpGateway = (c: VaisClient, id: string) => c.get<{ manifest: McpGatewayConfigManifest }>(`/v1/mcp-gateways/${id}`).then(r => r.manifest)
export const createMcpGateway = (c: VaisClient, body: unknown) => c.post<McpGatewayConfigManifest>('/v1/mcp-gateways', body)
export const validateMcpGateway = (c: VaisClient, body: unknown) => c.post<ValidateResult>('/v1/mcp-gateways/validate', body)
export const deleteMcpGateway = (c: VaisClient, id: string) => c.delete(`/v1/mcp-gateways/${id}`)

export const listMcpServers = (c: VaisClient) => c.get<ListResponse<McpServerManifest>>('/v1/mcp-servers').then(r => r.items)
export const getMcpServer = (c: VaisClient, id: string) => c.get<{ manifest: McpServerManifest }>(`/v1/mcp-servers/${id}`).then(r => r.manifest)
export const createMcpServer = (c: VaisClient, body: unknown) => c.post<McpServerManifest>('/v1/mcp-servers', body)
export const validateMcpServer = (c: VaisClient, body: unknown) => c.post<ValidateResult>('/v1/mcp-servers/validate', body)
export const deleteMcpServer = (c: VaisClient, id: string) => c.delete(`/v1/mcp-servers/${id}`)

import type { AnyManifest, ResourceKind } from './types'

export function getResource(c: VaisClient, kind: ResourceKind, id: string): Promise<AnyManifest> {
  switch (kind) {
    case 'agents': return getAgent(c, id)
    case 'graphs': return getGraph(c, id)
    case 'llm-gateways': return getLlmGateway(c, id)
    case 'mcp-gateways': return getMcpGateway(c, id)
    case 'mcp-servers': return getMcpServer(c, id)
  }
}

export function createResource(c: VaisClient, kind: ResourceKind, body: unknown): Promise<AnyManifest> {
  switch (kind) {
    case 'agents': return createAgent(c, body)
    case 'graphs': return createGraph(c, body)
    case 'llm-gateways': return createLlmGateway(c, body)
    case 'mcp-gateways': return createMcpGateway(c, body)
    case 'mcp-servers': return createMcpServer(c, body)
  }
}

export function validateResource(c: VaisClient, kind: ResourceKind, body: unknown): Promise<ValidateResult> {
  switch (kind) {
    case 'agents': return validateAgent(c, body)
    case 'graphs': return validateGraph(c, body)
    case 'llm-gateways': return validateLlmGateway(c, body)
    case 'mcp-gateways': return validateMcpGateway(c, body)
    case 'mcp-servers': return validateMcpServer(c, body)
  }
}

export function deleteResourceById(c: VaisClient, kind: ResourceKind, id: string): Promise<void> {
  switch (kind) {
    case 'agents': return deleteAgent(c, id)
    case 'graphs': return deleteGraph(c, id)
    case 'llm-gateways': return deleteLlmGateway(c, id)
    case 'mcp-gateways': return deleteMcpGateway(c, id)
    case 'mcp-servers': return deleteMcpServer(c, id)
  }
}

export function invokeResource(c: VaisClient, kind: 'agents' | 'graphs', id: string, body: unknown): Promise<Response> {
  return c.stream(`/v1/${kind}/${id}/invoke`, body)
}
