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
  mcpGatewayRef?: string
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

export interface GatewayEventDto {
  eventId: string
  gatewayId: string
  eventKind: string
  modelId: string | null
  inputTokens: number
  outputTokens: number
  durationMs: number | null
  cacheHit: boolean | null
  errorType: string | null
  at: string
  correlationId: string | null
  runId: string | null
  inputJson: string | null
  outputJson: string | null
}

export interface AgentRunDto {
  runId: string
  agentId: string
  source: 'graph' | 'standalone'
  nodeId: string | null
  nodeKind: string | null
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

export interface McpEventDto {
  eventId: string
  serverId: string
  toolName: string
  eventKind: string
  durationMs: number | null
  cacheHit: boolean
  blockedReason: string | null
  errorType: string | null
  at: string
  correlationId: string | null
  runId: string | null
  inputJson: string | null
  outputJson: string | null
}

export interface McpGatewayEventDto {
  eventId: string
  gatewayId: string
  toolName: string
  eventKind: string
  durationMs: number | null
  cacheHit: boolean
  blockedReason: string | null
  errorType: string | null
  at: string
  correlationId: string | null
  runId: string | null
  inputJson: string | null
  outputJson: string | null
}

export interface AgentLogEntryDto {
  entryId: string
  agentId: string
  runId: string | null
  at: string
  level: string
  message: string
  source: 'grain' | 'python'
}
