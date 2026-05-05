import React, { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useClient } from '../../api/useClient'
import { listAgentRuns } from '../../api/resources'
import { ApiError } from '../../api/client'
import type { AgentRunDto } from '../../api/types'
import '../../styles/logsTab.css'

interface Props {
  id: string
}

const NODE_STATUS_CLASS: Record<string, string> = {
  completed:   'lognode__status--completed',
  failed:      'lognode__status--failed',
  running:     'lognode__status--running',
  interrupted: 'lognode__status--interrupted',
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  const diff = Date.now() - d.getTime()
  if (diff < 60_000) return `${Math.round(diff / 1000)}s ago`
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

function AgentRunRow({ run }: { run: AgentRunDto }) {
  const [open, setOpen] = useState(false)
  const hasDetail = !!(run.inputText || run.outputText || run.error || run.edgesTaken?.length)

  return (
    <div className={`lognode${open ? ' lognode--open' : ''}`}>
      <div className="lognode__summary" onClick={() => setOpen(o => !o)} style={!hasDetail ? { cursor: 'default' } : undefined}>
        <ChevronRight className="lognode__chevron" style={!hasDetail ? { visibility: 'hidden' } : undefined} />
        <span className={`lognode__status ${NODE_STATUS_CLASS[run.status] ?? ''}`}>
          {run.status}
        </span>
        <span className="lognode__kind" style={run.source === 'standalone' ? { fontStyle: 'italic' } : undefined}>
          {run.nodeKind ?? 'invoke'}
        </span>
        <span
          className="lognode__badge"
          style={{
            fontSize: '10px',
            padding: '1px 5px',
            borderRadius: '3px',
            background: run.source === 'standalone' ? 'var(--color-accent, #4a90d9)' : 'var(--color-muted, #666)',
            color: '#fff',
            flexShrink: 0,
          }}
        >
          {run.source}
        </span>
        <span className="lognode__id" title={run.runId}>{run.runId}</span>
        <span className="lognode__meta">
          {run.durationMs != null && <span>{run.durationMs}ms</span>}
          {(run.inputTokens + run.outputTokens) > 0 && (
            <span>{run.inputTokens}+{run.outputTokens} tok</span>
          )}
          <span>{formatTime(run.startedAt)}</span>
        </span>
      </div>

      {open && hasDetail && (
        <div className="lognode__detail">
          {run.error && (
            <>
              <div className="lognode__section-label">Error</div>
              <div className="lognode__error">{run.error}</div>
            </>
          )}
          {run.edgesTaken && run.edgesTaken.length > 0 && (
            <div className="lognode__edges">edges: {run.edgesTaken.join(', ')}</div>
          )}
          {run.inputText && (
            <>
              <div className="lognode__section-label">Input</div>
              <pre className="lognode__text">{run.inputText}</pre>
            </>
          )}
          {run.outputText && (
            <>
              <div className="lognode__section-label">Output</div>
              <pre className="lognode__text">{run.outputText}</pre>
            </>
          )}
        </div>
      )}
    </div>
  )
}

export function AgentRunsTab({ id }: Props) {
  const client = useClient()

  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['agent-runs', id, client.baseUrl],
    queryFn: () => listAgentRuns(client, id),
    refetchInterval: 10_000,
  })

  const isUnconfigured = error instanceof ApiError && error.status === 503

  return (
    <div className="logs">
      <div className="logs__toolbar">
        <span>Invocations</span>
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
        {!isLoading && !error && data?.length === 0 && (
          <div className="logs__empty">No invocations recorded yet.</div>
        )}
        {!isLoading && !error && data?.map((run, i) => (
          <AgentRunRow key={`${run.runId}-${i}`} run={run} />
        ))}
      </div>
    </div>
  )
}
