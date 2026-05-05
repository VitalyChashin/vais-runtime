import { useQuery } from '@tanstack/react-query'
import { useClient } from '../../api/useClient'
import { listAgentLogs } from '../../api/resources'
import { ApiError } from '../../api/client'
import type { AgentLogEntryDto } from '../../api/types'
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

function levelColor(level: string): string {
  switch (level.toLowerCase()) {
    case 'error':
    case 'critical': return 'var(--color-error)'
    case 'warning': return 'var(--color-warning, #e6a817)'
    default: return 'inherit'
  }
}

function AgentLogRow({ entry }: { entry: AgentLogEntryDto }) {
  return (
    <div className="lognode">
      <div className="lognode__summary" style={{ cursor: 'default' }}>
        <span className="lognode__kind" style={{ minWidth: 80, color: levelColor(entry.level), fontSize: 11 }}>
          {entry.level}
        </span>
        <span className="lognode__kind" style={{ minWidth: 60, color: 'var(--color-muted, #888)', fontSize: 11 }}>
          {entry.source}
        </span>
        <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: 'monospace', fontSize: 12 }}>
          {entry.message}
        </span>
        <span className="lognode__meta">
          <span>{formatTime(entry.at)}</span>
        </span>
      </div>
    </div>
  )
}

export function AgentLogsTab({ id }: Props) {
  const client = useClient()

  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['agent-logs', id, client.baseUrl],
    queryFn: () => listAgentLogs(client, id),
    refetchInterval: 5_000,
  })

  const isUnconfigured = error instanceof ApiError && error.status === 503

  return (
    <div className="logs">
      <div className="logs__toolbar">
        <span>Agent logs</span>
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
          <div className="logs__empty">Agent log sink not configured on this runtime.</div>
        )}
        {error && !isUnconfigured && (
          <div className="logs__empty" style={{ color: 'var(--color-error)' }}>
            Failed to load logs
          </div>
        )}
        {!isLoading && !error && data?.length === 0 && (
          <div className="logs__empty">No log lines captured yet.</div>
        )}
        {!isLoading && !error && data?.map((entry, i) => (
          <AgentLogRow key={`${entry.entryId}-${i}`} entry={entry} />
        ))}
      </div>
    </div>
  )
}
