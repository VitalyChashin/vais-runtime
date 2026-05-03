import { useQuery } from '@tanstack/react-query'
import type { AgentManifest, ResourceKind } from '../../api/types'
import { useClient } from '../../api/useClient'
import { useSelection } from '../../store/selectionStore'
import { getResource, listAgents, listGraphs } from '../../api/resources'

interface Props {
  kind: ResourceKind
  id: string
}

interface Ref {
  kind: ResourceKind
  id: string
  label: string
}

function extractOutboundRefs(resource: Record<string, unknown>): Ref[] {
  const refs: Ref[] = []
  if (typeof resource.llmGatewayRef === 'string') {
    refs.push({ kind: 'llm-gateways', id: resource.llmGatewayRef, label: `LLM Gateway: ${resource.llmGatewayRef}` })
  }
  if (typeof resource.mcpGatewayRef === 'string') {
    refs.push({ kind: 'mcp-gateways', id: resource.mcpGatewayRef, label: `MCP Gateway: ${resource.mcpGatewayRef}` })
  }
  if (Array.isArray(resource.mcpServers)) {
    for (const s of resource.mcpServers as string[]) {
      refs.push({ kind: 'mcp-servers', id: s, label: `MCP Server: ${s}` })
    }
  }
  return refs
}

function referencesId(agent: AgentManifest, targetId: string): boolean {
  return (
    agent.llmGatewayRef === targetId ||
    agent.mcpGatewayRef === targetId ||
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    ((agent as any).mcpServers?.includes(targetId) ?? false)
  )
}

const SectionHeader = ({ children }: { children: string }) => (
  <h3 style={{
    fontSize: 10,
    fontWeight: 600,
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.08em',
    marginBottom: 8,
    marginTop: 0,
  }}>
    {children}
  </h3>
)

export function RefsTab({ kind, id }: Props) {
  const client = useClient()
  const { select } = useSelection()

  const { data: resource, isLoading: resourceLoading } = useQuery({
    queryKey: [kind, id, client.baseUrl],
    queryFn: () => getResource(client, kind, id),
  })

  const { data: agents = [] } = useQuery({
    queryKey: ['agents', client.baseUrl],
    queryFn: () => listAgents(client),
    refetchInterval: 5000,
  })

  const { data: graphs = [] } = useQuery({
    queryKey: ['graphs', client.baseUrl],
    queryFn: () => listGraphs(client),
    refetchInterval: 5000,
  })

  if (resourceLoading) {
    return (
      <div style={{ padding: 16, fontSize: 13, color: 'var(--color-text-muted)' }}>
        Loading…
      </div>
    )
  }

  const outbound = resource ? extractOutboundRefs(resource as unknown as Record<string, unknown>) : []
  const referencedBy: Ref[] = [
    ...agents
      .filter(a => referencesId(a, id))
      .map(a => ({ kind: 'agents' as ResourceKind, id: a.id, label: a.name || a.id })),
    ...graphs
      .filter(g => (g as AgentManifest).llmGatewayRef === id || (g as AgentManifest).mcpGatewayRef === id)
      .map(g => ({ kind: 'graphs' as ResourceKind, id: g.id, label: g.name || g.id })),
  ]

  const Muted = ({ children }: { children: string }) => (
    <p style={{ fontSize: 13, color: 'var(--color-text-muted)', margin: 0 }}>{children}</p>
  )

  return (
    <div style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 24 }}>
      <section>
        <SectionHeader>Outbound</SectionHeader>
        {outbound.length === 0 ? (
          <Muted>No references</Muted>
        ) : (
          <ul style={{ margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: 4 }}>
            {outbound.map(ref => (
              <li key={`${ref.kind}:${ref.id}`}>
                <button className="ref-link" onClick={() => select(ref.kind, ref.id)}>
                  {ref.label}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <SectionHeader>Referenced by</SectionHeader>
        {referencedBy.length === 0 ? (
          <Muted>Not referenced</Muted>
        ) : (
          <ul style={{ margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: 4 }}>
            {referencedBy.map(ref => (
              <li key={`${ref.kind}:${ref.id}`}>
                <button className="ref-link" onClick={() => select(ref.kind, ref.id)}>
                  {ref.label}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}
