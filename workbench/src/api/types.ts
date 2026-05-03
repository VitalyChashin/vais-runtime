export type ResourceKind =
  | 'agents'
  | 'graphs'
  | 'llm-gateways'
  | 'mcp-gateways'
  | 'mcp-servers'

export interface AgentManifest {
  id: string
  name: string
  llmGatewayRef?: string
  mcpGatewayRef?: string
  mcpServers?: string[]
}

export interface AgentGraphManifest {
  id: string
  name: string
}

export interface LlmGatewayConfigManifest {
  id: string
  name: string
}

export interface McpGatewayConfigManifest {
  id: string
  name: string
}

export interface McpServerManifest {
  id: string
  name: string
  virtual?: boolean
}

export type AnyManifest =
  | AgentManifest
  | AgentGraphManifest
  | LlmGatewayConfigManifest
  | McpGatewayConfigManifest
  | McpServerManifest

export interface ListResponse<T> {
  items: T[]
  nextCursor: string | null
}

export interface ValidateResult {
  valid: boolean
  errors?: string[]
}
