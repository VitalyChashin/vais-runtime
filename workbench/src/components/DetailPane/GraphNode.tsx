import { memo } from 'react'
import { Handle, Position } from '@xyflow/react'
import type { NodeProps } from '@xyflow/react'
import type { GraphNodeData } from './graphLayout'

const KIND_BORDER: Record<string, string> = {
  Agent:     'var(--color-accent)',
  Code:      'var(--color-border-strong)',
  End:       'var(--color-success)',
  Interrupt: 'var(--color-warn)',
}

const KIND_BG: Record<string, string> = {
  End:       'rgba(74,222,128,0.07)',
  Interrupt: 'rgba(251,191,36,0.07)',
}

export const GraphNode = memo(function GraphNode({ data }: NodeProps) {
  const d = data as GraphNodeData
  const border = KIND_BORDER[d.kind] ?? 'var(--color-border-strong)'
  const bg = KIND_BG[d.kind] ?? 'var(--color-bg-elevated)'

  return (
    <div style={{
      width: 180,
      height: 56,
      background: bg,
      border: `1px solid ${border}`,
      borderLeft: d.isEntry ? '3px solid var(--color-accent)' : `1px solid ${border}`,
      borderRadius: 6,
      padding: '6px 10px',
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      fontSize: 12,
      userSelect: 'none',
    }}>
      <Handle type="target" position={Position.Top} style={{ opacity: 0, pointerEvents: 'none' }} />
      <div style={{
        color: 'var(--color-text-primary)',
        fontWeight: 500,
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      }}>
        {d.nodeId}
      </div>
      <div style={{
        color: 'var(--color-text-muted)',
        fontSize: 11,
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      }}>
        {d.refId ?? d.kind}
      </div>
      <Handle type="source" position={Position.Bottom} style={{ opacity: 0, pointerEvents: 'none' }} />
    </div>
  )
})
