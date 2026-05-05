import { describe, it, expect } from 'vitest'
import { layoutGraph } from './graphLayout'
import type { AgentGraphManifest } from '../../api/types'

const manifest: AgentGraphManifest = {
  id: 'test-pipeline',
  name: 'Test Pipeline',
  entry: 'planner',
  nodes: [
    { id: 'planner',     kind: 'Agent', ref: { id: 'planner-agent' } },
    { id: 'researcher',  kind: 'Agent', ref: { id: 'research-agent' } },
    { id: 'analyst',     kind: 'Agent', ref: { id: 'sgr-analyst' } },
    { id: 'synthesizer', kind: 'Agent', ref: { id: 'synthesizer' } },
    { id: 'end',         kind: 'End' },
  ],
  edges: [
    { from: 'planner',     to: 'researcher',  concurrent: true },
    { from: 'planner',     to: 'analyst',     concurrent: true },
    { from: 'researcher',  to: 'synthesizer', concurrent: true },
    { from: 'analyst',     to: 'synthesizer', concurrent: true },
    { from: 'synthesizer', to: 'end' },
  ],
}

describe('layoutGraph', () => {
  it('returns the correct number of nodes and edges', () => {
    const { nodes, edges } = layoutGraph(manifest)
    expect(nodes).toHaveLength(5)
    expect(edges).toHaveLength(5)
  })

  it('marks only the entry node as isEntry', () => {
    const { nodes } = layoutGraph(manifest)
    const entryNodes = nodes.filter(n => n.data.isEntry)
    expect(entryNodes).toHaveLength(1)
    expect(entryNodes[0].id).toBe('planner')
  })

  it('assigns graphNode type to all nodes', () => {
    const { nodes } = layoutGraph(manifest)
    expect(nodes.every(n => n.type === 'graphNode')).toBe(true)
  })

  it('styles concurrent edges with dashed stroke', () => {
    const { edges } = layoutGraph(manifest)
    const concurrent = edges.filter(e => e.style?.strokeDasharray)
    expect(concurrent).toHaveLength(4)
  })

  it('assigns positions to all nodes', () => {
    const { nodes } = layoutGraph(manifest)
    for (const node of nodes) {
      expect(typeof node.position.x).toBe('number')
      expect(typeof node.position.y).toBe('number')
    }
  })

  it('handles empty manifest gracefully', () => {
    const { nodes, edges } = layoutGraph({ id: 'empty', name: 'Empty' })
    expect(nodes).toHaveLength(0)
    expect(edges).toHaveLength(0)
  })
})
