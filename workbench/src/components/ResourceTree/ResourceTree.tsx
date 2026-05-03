import { ResourceSection } from './ResourceSection'
import { useResourceList } from '../../hooks/useResourceList'
import {
  listAgents,
  listGraphs,
  listLlmGateways,
  listMcpGateways,
  listMcpServers,
} from '../../api/resources'

export function ResourceTree() {
  const agents = useResourceList('agents', listAgents)
  const graphs = useResourceList('graphs', listGraphs)
  const llmGateways = useResourceList('llm-gateways', listLlmGateways)
  const mcpGateways = useResourceList('mcp-gateways', listMcpGateways)
  const mcpServers = useResourceList('mcp-servers', listMcpServers)

  return (
    <nav>
      <ResourceSection kind="agents" label="Agents" {...agents} />
      <ResourceSection kind="graphs" label="Graphs" {...graphs} />
      <ResourceSection kind="llm-gateways" label="LLM Gateways" {...llmGateways} />
      <ResourceSection kind="mcp-gateways" label="MCP Gateways" {...mcpGateways} />
      <ResourceSection kind="mcp-servers" label="MCP Servers" {...mcpServers} />
    </nav>
  )
}
