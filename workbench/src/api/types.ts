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

export interface GraphEdge {
  from: string
  to: string
  concurrent?: boolean
}

export interface AgentGraphManifest {
  id: string
  name: string
  entry?: string
  nodes?: GraphNode[]
  edges?: GraphEdge[]
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

export interface PipelineRun {
  runId: string
  graphId: string
  status: string
  startedAt: string
  endedAt: string | null
  durationMs: number | null
  superSteps: number
  error: string | null
}

export interface NodeExecution {
  runId: string
  nodeId: string
  nodeKind: string
  agentId: string | null
  status: string
  startedAt: string
  endedAt: string | null
  durationMs: number | null
  inputText: string | null
  outputText: string | null
  inputTokens: number
  outputTokens: number
  error: string | null
  edgesTaken: string[] | null
}

export interface RunListResponse {
  items: PipelineRun[]
}
