import React, { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useClient } from '../../api/useClient'
import { listRuns, getRunNodes } from '../../api/resources'
import { ApiError } from '../../api/client'
import type { PipelineRun, NodeExecution } from '../../api/types'
import '../../styles/logsTab.css'

interface Props {
  id: string
}

const STATUS_CLASS: Record<string, string> = {
  completed:   'logrun__status--completed',
  failed:      'logrun__status--failed',
  running:     'logrun__status--running',
  interrupted: 'logrun__status--interrupted',
}

const NODE_STATUS_CLASS: Record<string, string> = {
  completed:   'lognode__status--completed',
  failed:      'lognode__status--failed',
  running:     'lognode__status--running',
  interrupted: 'lognode__status--interrupted',
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  const now = Date.now()
  const diff = now - d.getTime()
  if (diff < 60_000)  return `${Math.round(diff / 1000)}s ago`
  if (diff < 3_600_000) return `${Math.round(diff / 60_000)}m ago`
  if (diff < 86_400_000) return `${Math.round(diff / 3_600_000)}h ago`
  return d.toLocaleDateString()
}

function ChevronRight({ className, style }: { className: string; style?: React.CSSProperties }) {
  return (
    <svg className={className} style={style} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <path d="m9 18 6-6-6-6"/>
    </svg>
  )
}

function RefreshIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/>
      <path d="M21 3v5h-5"/>
      <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/>
      <path d="M8 16H3v5"/>
    </svg>
  )
}

function NodeRow({ node }: { node: NodeExecution }) {
  const [open, setOpen] = useState(false)
  const hasDetail = !!(node.inputText || node.outputText || node.error || node.edgesTaken?.length)

  return (
    <div className={`lognode${open ? ' lognode--open' : ''}`}>
      <div className="lognode__summary" onClick={() => setOpen(o => !o)} style={!hasDetail ? { cursor: 'default' } : undefined}>
        <ChevronRight className="lognode__chevron" style={!hasDetail ? { visibility: 'hidden' } : undefined} />
        <span className={`lognode__status ${NODE_STATUS_CLASS[node.status] ?? ''}`}>
          {node.status}
        </span>
        <span className="lognode__id" title={node.nodeId}>{node.nodeId}</span>
        <span className="lognode__kind">{node.nodeKind}</span>
        <span className="lognode__meta">
          {node.durationMs != null && <span>{node.durationMs}ms</span>}
          {(node.inputTokens + node.outputTokens) > 0 && (
            <span>{node.inputTokens}+{node.outputTokens} tok</span>
          )}
        </span>
      </div>

      {open && hasDetail && (
        <div className="lognode__detail">
          {node.error && (
            <>
              <div className="lognode__section-label">Error</div>
              <div className="lognode__error">{node.error}</div>
            </>
          )}
          {node.edgesTaken && node.edgesTaken.length > 0 && (
            <div className="lognode__edges">edges: {node.edgesTaken.join(', ')}</div>
          )}
          {node.inputText && (
            <>
              <div className="lognode__section-label">Input</div>
              <pre className="lognode__text">{node.inputText}</pre>
            </>
          )}
          {node.outputText && (
            <>
              <div className="lognode__section-label">Output</div>
              <pre className="lognode__text">{node.outputText}</pre>
            </>
          )}
        </div>
      )}
    </div>
  )
}

function RunRow({ run, graphId }: { run: PipelineRun; graphId: string }) {
  const [open, setOpen] = useState(false)
  const client = useClient()
  const isRunning = run.status === 'running'

  const { data: nodes } = useQuery({
    queryKey: ['run-nodes', graphId, run.runId, client.baseUrl],
    queryFn: () => getRunNodes(client, graphId, run.runId),
    enabled: open,
    refetchInterval: isRunning ? 3_000 : false,
  })

  return (
    <div className={`logrun${open ? ' logrun--open' : ''}`}>
      <div className="logrun__summary" onClick={() => setOpen(o => !o)}>
        <ChevronRight className="logrun__chevron" />
        <span className={`logrun__status ${STATUS_CLASS[run.status] ?? ''}`}>
          {run.status}
        </span>
        <span className="logrun__id" title={run.runId}>{run.runId}</span>
        <span className="logrun__meta">
          {run.durationMs != null && <span>{run.durationMs}ms</span>}
          <span>{run.superSteps} steps</span>
          <span>{formatTime(run.startedAt)}</span>
        </span>
      </div>

      {open && (
        <div className="logrun__nodes">
          {!nodes && (
            <div className="lognode__summary" style={{ cursor: 'default', color: 'var(--color-text-muted)' }}>
              Loading nodes…
            </div>
          )}
          {nodes && nodes.length === 0 && (
            <div className="lognode__summary" style={{ cursor: 'default', fontStyle: 'italic', color: 'var(--color-text-muted)' }}>
              No nodes recorded
            </div>
          )}
          {nodes && nodes.map(n => (
            <NodeRow key={n.nodeId} node={n} />
          ))}
        </div>
      )}
    </div>
  )
}

export function LogsTab({ id }: Props) {
  const client = useClient()

  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['runs', id, client.baseUrl],
    queryFn: () => listRuns(client, id),
    refetchInterval: 10_000,
  })

  const isUnconfigured = error instanceof ApiError && error.status === 503

  return (
    <div className="logs">
      <div className="logs__toolbar">
        <span>Run history</span>
        <div className="logs__toolbar-spacer" />
        <button
          className="btn btn--bare btn--icon"
          title="Refresh"
          onClick={() => refetch()}
          disabled={isFetching}
        >
          <RefreshIcon />
        </button>
      </div>

      <div className="logs__scroll">
        {isLoading && (
          <div className="logs__empty">Loading…</div>
        )}
        {isUnconfigured && (
          <div className="logs__empty">Run store not configured on this runtime.</div>
        )}
        {error && !isUnconfigured && (
          <div className="logs__empty" style={{ color: 'var(--color-error)' }}>
            Failed to load runs
          </div>
        )}
        {!isLoading && !error && data?.items.length === 0 && (
          <div className="logs__empty">No runs recorded yet.</div>
        )}
        {!isLoading && !error && data?.items.map(run => (
          <RunRow key={run.runId} run={run} graphId={id} />
        ))}
      </div>
    </div>
  )
}
