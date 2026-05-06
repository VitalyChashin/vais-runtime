import React, { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useClient } from '../../api/useClient'
import { listGatewayEvents } from '../../api/resources'
import { ApiError } from '../../api/client'
import type { GatewayEventDto } from '../../api/types'
import '../../styles/logsTab.css'

interface Props {
  id: string
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  const diff = Date.now() - d.getTime()
  if (diff < 60_000) return `${Math.round(diff / 1000)}s ago`
  if (diff < 3_600_000) return `${Math.round(diff / 60_000)}m ago`
  if (diff < 86_400_000) return `${Math.round(diff / 3_600_000)}h ago`
  return d.toLocaleDateString()
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

function ChevronRight({ className, style }: { className: string; style?: React.CSSProperties }) {
  return (
    <svg className={className} style={style} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <path d="m9 18 6-6-6-6"/>
    </svg>
  )
}

function prettyJson(s: string): string {
  try { return JSON.stringify(JSON.parse(s), null, 2) } catch { return s }
}

function PayloadBlock({ label, value }: { label: string; value: string }) {
  return (
    <>
      <div className="lognode__section-label">{label}</div>
      <pre style={{ margin: '2px 0 6px', padding: '6px 8px', background: 'var(--color-surface, #1e1e1e)', borderRadius: 4, fontSize: 11, overflowX: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all', maxHeight: 300 }}>
        {prettyJson(value)}
      </pre>
    </>
  )
}

function GatewayEventRow({ evt }: { evt: GatewayEventDto }) {
  const [open, setOpen] = useState(false)
  const failed = evt.eventKind === 'completion.failed'
  const hasDetail = !!(evt.errorType || evt.correlationId || evt.runId || evt.inputJson || evt.outputJson)

  return (
    <div className={`lognode${open ? ' lognode--open' : ''}`}>
      <div
        className="lognode__summary"
        onClick={() => setOpen(o => !o)}
        style={!hasDetail ? { cursor: 'default' } : undefined}
      >
        <ChevronRight className="lognode__chevron" style={!hasDetail ? { visibility: 'hidden' } : undefined} />
        <span
          className="lognode__status"
          style={{ color: failed ? 'var(--color-error)' : 'var(--color-success)' }}
        >
          {failed ? 'failed' : 'ok'}
        </span>
        <span className="lognode__kind" style={{ minWidth: 120 }}>
          {evt.modelId ?? '—'}
        </span>
        <span className="lognode__meta">
          {evt.durationMs != null && <span>{evt.durationMs}ms</span>}
          {(evt.inputTokens + evt.outputTokens) > 0 && (
            <span>{evt.inputTokens}+{evt.outputTokens} tok</span>
          )}
          {evt.cacheHit && (
            <span style={{ color: 'var(--color-accent, #4a90d9)', fontSize: 10 }}>cache</span>
          )}
          <span>{formatTime(evt.at)}</span>
        </span>
      </div>

      {open && hasDetail && (
        <div className="lognode__detail">
          {evt.errorType && (
            <>
              <div className="lognode__section-label">Error</div>
              <div className="lognode__error">{evt.errorType}</div>
            </>
          )}
          {evt.inputJson && <PayloadBlock label="Input" value={evt.inputJson} />}
          {evt.outputJson && <PayloadBlock label="Output" value={evt.outputJson} />}
          {evt.correlationId && (
            <div className="lognode__edges">correlation: {evt.correlationId}</div>
          )}
          {evt.runId && (
            <div className="lognode__edges">run: {evt.runId}</div>
          )}
        </div>
      )}
    </div>
  )
}

export function GatewayEventsTab({ id }: Props) {
  const client = useClient()

  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['gateway-events', id, client.baseUrl],
    queryFn: () => listGatewayEvents(client, id),
    refetchInterval: 10_000,
  })

  const isUnconfigured = error instanceof ApiError && error.status === 503

  return (
    <div className="logs">
      <div className="logs__toolbar">
        <span>Completion events</span>
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
          <div className="logs__empty">Gateway event store not configured on this runtime.</div>
        )}
        {error && !isUnconfigured && (
          <div className="logs__empty" style={{ color: 'var(--color-error)' }}>
            Failed to load events
          </div>
        )}
        {!isLoading && !error && data?.length === 0 && (
          <div className="logs__empty">No completion events recorded yet.</div>
        )}
        {!isLoading && !error && data?.map((evt, i) => (
          <GatewayEventRow key={`${evt.eventId}-${i}`} evt={evt} />
        ))}
      </div>
    </div>
  )
}
