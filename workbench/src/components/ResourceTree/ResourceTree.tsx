import { useQuery } from '@tanstack/react-query'
import { ResourceSection } from './ResourceSection'
import { PluginSection } from './PluginSection'
import { useResourceList } from '../../hooks/useResourceList'
import {
  listAgents,
  listGraphs,
  listLlmGateways,
  listMcpGateways,
  listMcpServers,
  listPlugins,
} from '../../api/resources'
import { useClient } from '../../api/useClient'

export function ResourceTree() {
  const client = useClient()
  const agents = useResourceList('agents', listAgents)
  const graphs = useResourceList('graphs', listGraphs)
  const llmGateways = useResourceList('llm-gateways', listLlmGateways)
  const mcpGateways = useResourceList('mcp-gateways', listMcpGateways)
  const mcpServers = useResourceList('mcp-servers', listMcpServers)
  const pluginsQuery = useQuery({
    queryKey: ['plugins', client.baseUrl],
    queryFn: () => listPlugins(client),
    refetchInterval: 5000,
  })

  return (
    <nav>
      <ResourceSection kind="agents" label="Agents" {...agents} />
      <ResourceSection kind="graphs" label="Graphs" {...graphs} />
      <ResourceSection kind="llm-gateways" label="LLM Gateways" {...llmGateways} />
      <ResourceSection kind="mcp-gateways" label="MCP Gateways" {...mcpGateways} />
      <ResourceSection kind="mcp-servers" label="MCP Servers" {...mcpServers} />
      <PluginSection
        data={Array.isArray(pluginsQuery.data) ? pluginsQuery.data : []}
        isLoading={pluginsQuery.isLoading}
        error={pluginsQuery.error as Error | null}
      />
    </nav>
  )
}
