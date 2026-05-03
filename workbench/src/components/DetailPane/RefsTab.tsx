import '../../styles/refsTab.css'
import { useQuery } from '@tanstack/react-query'
import type { AgentManifest, ResourceKind, SelectionKind } from '../../api/types'
import { useClient } from '../../api/useClient'
import { useSelection } from '../../store/selectionStore'
import { getResource, listAgents, listGraphs, listPlugins } from '../../api/resources'

interface Props {
  kind: ResourceKind
  id: string
}

interface InboundRef {
  kind: ResourceKind
  id: string
  label: string
}

type OutboundRow =
  | { field: string; targetKind: SelectionKind; id: string }
  | { field: string; targetKind: null; id: null; text?: string }

const KIND_LABEL: Record<string, string> = {
  agents: 'Agent',
  graphs: 'Graph',
  'llm-gateways': 'LLM Gateway',
  'mcp-gateways': 'MCP Gateway',
  'mcp-servers': 'MCP Server',
  plugins: 'Plugin',
}

function buildOutboundRows(kind: ResourceKind, resource: Record<string, unknown>): OutboundRow[] {
  if (kind === 'agents') {
    const rows: OutboundRow[] = []
    rows.push(
      typeof resource.llmGatewayRef === 'string'
        ? { field: 'llmGatewayRef', targetKind: 'llm-gateways', id: resource.llmGatewayRef }
        : { field: 'llmGatewayRef', targetKind: null, id: null }
    )
    rows.push(
      typeof resource.mcpGatewayRef === 'string'
        ? { field: 'mcpGatewayRef', targetKind: 'mcp-gateways', id: resource.mcpGatewayRef }
        : { field: 'mcpGatewayRef', targetKind: null, id: null }
    )
    const servers = resource.mcpServers as string[] | null
    if (Array.isArray(servers) && servers.length > 0) {
      servers.forEach((s, i) => rows.push({ field: `mcpServers[${i}]`, targetKind: 'mcp-servers', id: s }))
    } else {
      rows.push({ field: 'mcpServers', targetKind: null, id: null })
    }
    return rows
  }
  if (kind === 'graphs') {
    const nodes = (resource.nodes as Array<{ id: string; ref?: { id: string } }>) ?? []
    return nodes
      .filter(n => n.ref?.id)
      .map(n => ({ field: `nodes.${n.id}`, targetKind: 'agents' as ResourceKind, id: n.ref!.id }))
  }
  return []
}

function referencesId(agent: AgentManifest, targetId: string): boolean {
  return (
    agent.llmGatewayRef === targetId ||
    agent.mcpGatewayRef === targetId ||
    (agent.mcpServers?.includes(targetId) ?? false)
  )
}

const ArrowRight = () => (
  <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>
  </svg>
)

const ArrowLeft = () => (
  <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ transform: 'rotate(180deg)' }}>
    <path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>
  </svg>
)

export function RefsTab({ kind, id }: Props) {
  const client = useClient()
  const { select } = useSelection()

  const { data: resource, isLoading } = useQuery({
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

  const { data: plugins = [] } = useQuery({
    queryKey: ['plugins', client.baseUrl],
    queryFn: () => listPlugins(client),
    refetchInterval: 5000,
  })

  if (isLoading) {
    return (
      <div className="refs">
        <span className="refs__empty">Loading…</span>
      </div>
    )
  }

  const raw = resource as unknown as Record<string, unknown>
  const baseOutboundRows = resource ? buildOutboundRows(kind, raw) : []

  // For agents: prepend a handler row linking to the backing plugin.
  let handlerRow: OutboundRow | null = null
  if (kind === 'agents' && resource) {
    const typeName = (raw.handler as { typeName?: string } | undefined)?.typeName
    if (typeName) {
      const plugin = plugins.find(p => p.handlers.includes(typeName))
      handlerRow = plugin
        ? { field: 'handler', targetKind: 'plugins', id: plugin.name }
        : { field: 'handler', targetKind: null, id: null, text: typeName }
    } else {
      handlerRow = { field: 'handler', targetKind: null, id: null }
    }
  }

  const outboundRows: OutboundRow[] = handlerRow ? [handlerRow, ...baseOutboundRows] : baseOutboundRows
  const outboundCount = outboundRows.filter(r => r.id !== null).length

  const referencedBy: InboundRef[] = [
    ...agents
      .filter(a => referencesId(a, id))
      .map(a => ({ kind: 'agents' as ResourceKind, id: a.id, label: a.name || a.id })),
    ...graphs
      .filter(g => {
        if ((g as unknown as AgentManifest).llmGatewayRef === id) return true
        if ((g as unknown as AgentManifest).mcpGatewayRef === id) return true
        return g.nodes?.some(n => n.ref?.id === id) ?? false
      })
      .map(g => ({ kind: 'graphs' as ResourceKind, id: g.id, label: g.name || g.id })),
  ]

  const kindGroups = [
    { label: 'Agents', items: referencedBy.filter(r => r.kind === 'agents') },
    { label: 'Graphs', items: referencedBy.filter(r => r.kind === 'graphs') },
  ].filter(g => g.items.length > 0)

  return (
    <div className="refs">

      <div className="refs__group">
        <div className="refs__heading">
          <ArrowRight />
          Outbound references
          {outboundCount > 0 && <span className="refs__heading-count">{outboundCount}</span>}
        </div>
        {outboundRows.length === 0 ? (
          <span className="refs__empty">—</span>
        ) : (
          <div>
            {outboundRows.map(row => (
              <div className="refs__row" key={row.field}>
                <div className="refs__label">{row.field}</div>
                <div className="refs__value">
                  {row.id !== null ? (
                    <>
                      <button className="reflink" onClick={() => select(row.targetKind!, row.id!)}>
                        {row.id}
                        <span className="reflink__arrow">→</span>
                      </button>
                      <span className="reflink__kind">{KIND_LABEL[row.targetKind!] ?? row.targetKind}</span>
                    </>
                  ) : (
                    <span className="refs__empty">{'text' in row && row.text ? row.text : '—'}</span>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="refs__group">
        <div className="refs__heading">
          <ArrowLeft />
          Referenced by
          {referencedBy.length > 0 && <span className="refs__heading-count">{referencedBy.length}</span>}
        </div>
        {kindGroups.length === 0 ? (
          <span className="refs__empty">—</span>
        ) : (
          <div>
            {kindGroups.map(group => (
              <div className="refs__inbound-kind-group" key={group.label}>
                <div className="refs__inbound-kind-label">
                  {group.label}
                  <span className="refs__inbound-kind-label-count">{group.items.length}</span>
                </div>
                <div className="refs__inbound-list">
                  {group.items.map(ref => (
                    <div
                      className="refs__inbound-item"
                      key={`${ref.kind}:${ref.id}`}
                      onClick={() => select(ref.kind, ref.id)}
                    >
                      <span className="refs__inbound-bullet">·</span>
                      <button className="reflink">{ref.label}</button>
                    </div>
                  ))}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

    </div>
  )
}
