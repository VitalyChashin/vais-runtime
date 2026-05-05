import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ReactFlow, Background, Controls, BackgroundVariant } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import '../../styles/graphTab.css'
import { useClient } from '../../api/useClient'
import { getGraph } from '../../api/resources'
import { layoutGraph } from './graphLayout'
import { GraphNode } from './GraphNode'
import type { AgentGraphManifest } from '../../api/types'

const NODE_TYPES = { graphNode: GraphNode }

interface Props {
  id: string
}

export function GraphTab({ id }: Props) {
  const client = useClient()

  const { data, isLoading, error } = useQuery({
    queryKey: ['graph', id, client.baseUrl],
    queryFn: () => getGraph(client, id),
  })

  const { nodes, edges } = useMemo(
    () => data ? layoutGraph(data as AgentGraphManifest) : { nodes: [], edges: [] },
    [data],
  )

  if (isLoading) {
    return (
      <div style={{ padding: 16, fontSize: 13, color: 'var(--color-text-muted)' }}>
        Loading…
      </div>
    )
  }
  if (error || !data) {
    return (
      <div style={{ padding: 16, fontSize: 13, color: 'var(--color-error)' }}>
        Failed to load graph
      </div>
    )
  }

  return (
    <div style={{ width: '100%', height: '100%' }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={NODE_TYPES}
        fitView
        fitViewOptions={{ padding: 0.25 }}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        style={{ background: 'var(--color-bg-inset)' }}
      >
        <Background
          color="rgba(255,255,255,0.04)"
          variant={BackgroundVariant.Dots}
        />
        <Controls
          showInteractive={false}
          style={{
            background: 'var(--color-bg-elevated)',
            border: '1px solid var(--color-border)',
          }}
        />
      </ReactFlow>
    </div>
  )
}
