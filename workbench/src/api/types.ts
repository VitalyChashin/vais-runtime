export type ResourceKind =
  | 'agents'
  | 'graphs'
  | 'llm-gateways'
  | 'mcp-gateways'
  | 'mcp-servers'

export type SelectionKind = ResourceKind | 'plugins'

export interface PluginInfo {
  name: string
  kind: 'Assembly' | 'Python'
  state: 'Loading' | 'Ready' | 'Restarting' | 'Unavailable'
  processId?: number | null
  handlers: string[]
  toolNames?: string[] | null
  lastErrorSnippet?: string | null
}

export interface PluginSourcePushResponse {
  pluginName: string
  status: 'Success' | 'HandshakeFailed' | 'HandlerTypeNameChanged' | 'NoSupervisor' | 'ReloadDisabled' | 'UnpackFailed' | 'ScanFailed'
  processId?: number | null
  errorMessage?: string | null
}

export interface AgentHandlerRef {
  typeName: string
  assemblyName?: string | null
}

export interface AgentManifest {
  id: string
  name: string
  handler?: AgentHandlerRef
  llmGatewayRef?: string
  mcpGatewayRef?: string
  mcpServers?: string[]
}

export interface GraphNodeRef {
  id: string
}

export interface GraphNode {
  id: string
  kind: string
  ref?: GraphNodeRef
}

export interface AgentGraphManifest {
  id: string
  name: string
  nodes?: GraphNode[]
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
