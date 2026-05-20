// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

import '../../styles/refsTab.css'
import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useClient } from '../../api/useClient'
import { listExtensions, getExtensionMetrics } from '../../api/resources'

interface Props {
  extensionId: string
}

export function ExtensionDetail({ extensionId }: Props) {
  const client = useClient()

  const { data: extensions = [], isLoading } = useQuery({
    queryKey: ['extensions', client.baseUrl],
    queryFn: () => listExtensions(client),
    refetchInterval: 5000,
  })

  const { data: metrics } = useQuery({
    queryKey: ['extension-metrics', client.baseUrl, extensionId],
    queryFn: () => getExtensionMetrics(client, extensionId).catch(() => null),
    refetchInterval: 10000,
  })

  const ext = extensions.find(e => e.extensionId === extensionId)

  if (isLoading) {
    return <div style={{ padding: 16, fontSize: 13, color: 'var(--color-text-muted)' }}>Loading…</div>
  }
  if (!ext) {
    return (
      <div style={{ padding: 16, fontSize: 13, color: 'var(--color-text-muted)' }}>
        Extension <code>{extensionId}</code> not loaded.
      </div>
    )
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div className="toolbar">
        <div className="toolbar__title">
          <span>{ext.extensionId}</span>
          <span className="toolbar__sep">·</span>
          <span className="kind">v{ext.version}</span>
          <span className="toolbar__sep">·</span>
          <span className="kind">{ext.host}</span>
        </div>
      </div>

      <div style={{ padding: '12px 16px', display: 'flex', flexDirection: 'column', gap: 8 }}>
        <InfoRow label="Handlers">
          {ext.handlers.length === 0 ? (
            <span style={{ color: 'var(--color-text-muted)' }}>—</span>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
              {ext.handlers.map(h => (
                <span key={`${h.handlerId}-${h.seam}`} style={{ fontFamily: 'monospace', fontSize: 12 }}>
                  {h.handlerId} <span style={{ color: 'var(--color-text-muted)' }}>({h.seam}, p={h.priority}, f={h.failureMode})</span>
                </span>
              ))}
            </div>
          )}
        </InfoRow>

        {metrics && metrics.handlers.length > 0 && (
          <div style={{ marginTop: 12 }}>
            <div style={{ fontSize: 11, color: 'var(--color-text-muted)', marginBottom: 6, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
              Metrics (5-min window)
            </div>
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
              <thead>
                <tr style={{ color: 'var(--color-text-muted)' }}>
                  <th style={TH}>Handler</th>
                  <th style={TH}>Seam</th>
                  <th style={{ ...TH, textAlign: 'right' }}>P50 (s)</th>
                  <th style={{ ...TH, textAlign: 'right' }}>P95 (s)</th>
                  <th style={{ ...TH, textAlign: 'right' }}>Err %</th>
                  <th style={{ ...TH, textAlign: 'right' }}>Count</th>
                </tr>
              </thead>
              <tbody>
                {metrics.handlers.map(h => (
                  <tr key={`${h.handlerId}-${h.seam}`}>
                    <td style={TD}><code>{h.handlerId}</code></td>
                    <td style={TD}>{h.seam}</td>
                    <td style={{ ...TD, textAlign: 'right' }}>{h.p50Seconds.toFixed(3)}</td>
                    <td style={{ ...TD, textAlign: 'right' }}>{h.p95Seconds.toFixed(3)}</td>
                    <td style={{ ...TD, textAlign: 'right', color: h.errorRate > 0.1 ? 'var(--color-error)' : 'inherit' }}>
                      {(h.errorRate * 100).toFixed(1)}
                    </td>
                    <td style={{ ...TD, textAlign: 'right' }}>{h.totalInvocations}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  )
}

const TH: React.CSSProperties = {
  textAlign: 'left',
  padding: '2px 6px',
  fontWeight: 500,
  fontSize: 11,
}

const TD: React.CSSProperties = {
  padding: '3px 6px',
  borderTop: '1px solid var(--color-border)',
}

function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 8, fontSize: 13, alignItems: 'baseline' }}>
      <span style={{ color: 'var(--color-text-muted)', minWidth: 72, flexShrink: 0 }}>{label}</span>
      <span>{children}</span>
    </div>
  )
}
