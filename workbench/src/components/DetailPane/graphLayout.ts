import dagre from '@dagrejs/dagre'
import type { Node, Edge } from '@xyflow/react'
import type { AgentGraphManifest } from '../../api/types'

const NODE_W = 180
const NODE_H = 56

export interface GraphNodeData extends Record<string, unknown> {
  nodeId: string
  kind: string
  refId?: string
  isEntry: boolean
}

export function layoutGraph(manifest: AgentGraphManifest): {
  nodes: Node<GraphNodeData>[]
  edges: Edge[]
} {
  const g = new dagre.graphlib.Graph()
  g.setDefaultEdgeLabel(() => ({}))
  g.setGraph({ rankdir: 'TB', ranksep: 64, nodesep: 40 })

  for (const node of manifest.nodes ?? []) {
    g.setNode(node.id, { width: NODE_W, height: NODE_H })
  }
  for (const edge of manifest.edges ?? []) {
    g.setEdge(edge.from, edge.to)
  }

  dagre.layout(g)

  const nodes: Node<GraphNodeData>[] = (manifest.nodes ?? []).map(node => {
    const pos = g.node(node.id)
    return {
      id: node.id,
      type: 'graphNode',
      position: { x: pos.x - NODE_W / 2, y: pos.y - NODE_H / 2 },
      data: {
        nodeId: node.id,
        kind: node.kind,
        refId: node.ref?.id,
        isEntry: node.id === manifest.entry,
      },
    }
  })

  const edges: Edge[] = (manifest.edges ?? []).map((edge, i) => ({
    id: `e${i}-${edge.from}-${edge.to}`,
    source: edge.from,
    target: edge.to,
    style: edge.concurrent
      ? { stroke: 'var(--color-accent)', strokeDasharray: '4 3', strokeWidth: 1.5 }
      : { stroke: 'rgba(255,255,255,0.25)', strokeWidth: 1.5 },
    type: 'smoothstep',
  }))

  return { nodes, edges }
}
