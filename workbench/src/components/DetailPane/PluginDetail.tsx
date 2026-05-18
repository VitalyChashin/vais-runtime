// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

import '../../styles/refsTab.css'
import { useState } from 'react'
import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useClient } from '../../api/useClient'
import { listPlugins, listAgents } from '../../api/resources'
import { useSelection } from '../../store/selectionStore'

interface Props {
  pluginName: string
}

const STATE_COLOR: Record<string, string> = {
  Ready: 'var(--color-success)',
  Loading: 'var(--color-warn)',
  Restarting: 'var(--color-warn)',
  Unavailable: 'var(--color-error)',
}

export function PluginDetail({ pluginName }: Props) {
  const client = useClient()
  const { select } = useSelection()
  const [pushing, setPushing] = useState(false)
  const [pushResult, setPushResult] = useState<{ ok: boolean; message: string } | null>(null)
  const [pushingDll, setPushingDll] = useState(false)

  const { data: plugins = [], isLoading } = useQuery({
    queryKey: ['plugins', client.baseUrl],
    queryFn: () => listPlugins(client),
    refetchInterval: 5000,
  })

  const { data: agents = [] } = useQuery({
    queryKey: ['agents', client.baseUrl],
    queryFn: () => listAgents(client),
    refetchInterval: 5000,
  })

  const plugin = plugins.find(p => p.name === pluginName)

  async function handlePush() {
    setPushing(true)
    setPushResult(null)
    try {
      const res = await window.vais.pushPluginSource(pluginName, client.baseUrl)
      if ('cancelled' in res && res.cancelled) {
        // user dismissed dialog — no feedback
      } else if (res.status === 'Success') {
        setPushResult({ ok: true, message: `Reloaded (PID ${res.processId ?? '?'})` })
      } else {
        setPushResult({ ok: false, message: res.errorMessage ?? res.status })
      }
    } catch (err) {
      setPushResult({ ok: false, message: String(err) })
    } finally {
      setPushing(false)
    }
  }

  async function handleDllPush() {
    setPushingDll(true)
    setPushResult(null)
    try {
      const res = await window.vais.pushPluginDll(pluginName, client.baseUrl)
      if ('cancelled' in res && res.cancelled) {
        // user dismissed dialog — no feedback
      } else if (res.status === 'Success' || res.status === 'Bootstrapped') {
        const verb = res.status === 'Bootstrapped' ? 'bootstrapped' : 'reloaded'
        const handlers = res.handlers?.join(', ') ?? '—'
        setPushResult({ ok: true, message: `${verb} (handlers: ${handlers})` })
      } else {
        setPushResult({ ok: false, message: res.errorMessage ?? res.status })
      }
    } catch (err) {
      setPushResult({ ok: false, message: String(err) })
    } finally {
      setPushingDll(false)
    }
  }

  if (isLoading) {
    return <div style={{ padding: 16, fontSize: 13, color: 'var(--color-text-muted)' }}>Loading…</div>
  }
  if (!plugin) {
    return (
      <div style={{ padding: 16, fontSize: 13, color: 'var(--color-text-muted)' }}>
        Plugin <code>{pluginName}</code> not found.
      </div>
    )
  }

  const backedAgents = agents.filter(a => {
    const typeName = a.handler?.typeName
    return typeName != null && plugin.handlers.includes(typeName)
  })

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div className="toolbar">
        <div className="toolbar__title">
          <span>{plugin.name}</span>
          <span className="toolbar__sep">·</span>
          <span className="kind">{plugin.kind}</span>
        </div>
        <div className="toolbar__spacer" />
        {plugin.kind === 'Python' && (
          <div className="toolbar__actions">
            <button className="btn btn--ghost" disabled={pushing} onClick={handlePush}>
              {pushing ? 'Pushing…' : 'Push source…'}
            </button>
          </div>
        )}
        {plugin.kind === 'Assembly' && (
          <div className="toolbar__actions">
            <button className="btn btn--ghost" disabled={pushingDll} onClick={handleDllPush}>
              {pushingDll ? 'Pushing…' : 'Push DLL…'}
            </button>
          </div>
        )}
      </div>

      <div style={{ padding: '12px 16px', display: 'flex', flexDirection: 'column', gap: 8 }}>
        <InfoRow label="Language">
          <span>{plugin.kind === 'Assembly' ? 'C#' : plugin.kind === 'Python' ? 'Python' : plugin.kind}</span>
        </InfoRow>

        <InfoRow label="State">
          <span style={{ color: STATE_COLOR[plugin.state] ?? 'currentColor' }}>● {plugin.state}</span>
        </InfoRow>

        {plugin.targetApiVersion && (
          <InfoRow label="API ver."><code>{plugin.targetApiVersion}</code></InfoRow>
        )}

        {plugin.processId != null && (
          <InfoRow label="PID"><code>{plugin.processId}</code></InfoRow>
        )}

        {plugin.handlers.length > 0 && (
          <InfoRow label="Handlers">
            <span style={{ fontFamily: 'monospace', fontSize: 12 }}>{plugin.handlers.join(', ')}</span>
          </InfoRow>
        )}

        {plugin.toolNames && plugin.toolNames.length > 0 && (
          <InfoRow label="Tools">
            <span style={{ fontFamily: 'monospace', fontSize: 12 }}>{plugin.toolNames.join(', ')}</span>
          </InfoRow>
        )}

        {plugin.lastErrorSnippet && (
          <div style={{
            marginTop: 4,
            padding: '8px 10px',
            background: 'var(--color-error-subtle)',
            borderRadius: 4,
            fontFamily: 'monospace',
            fontSize: 11,
            color: 'var(--color-error)',
            whiteSpace: 'pre-wrap',
          }}>
            {plugin.lastErrorSnippet}
          </div>
        )}

        {pushResult && (
          <div className={pushResult.ok ? 'vmsg vmsg--ok' : 'vmsg vmsg--err'} style={{ marginTop: 4 }}>
            {pushResult.ok ? '✓ ' : '✗ '}{pushResult.message}
          </div>
        )}

        <div style={{ marginTop: 16, display: 'flex', gap: 8, fontSize: 13, alignItems: 'flex-start' }}>
          <span style={{ color: 'var(--color-text-muted)', minWidth: 72, flexShrink: 0, paddingTop: backedAgents.length > 1 ? 2 : 0 }}>
            Used by
          </span>
          {backedAgents.length === 0 ? (
            <span style={{ color: 'var(--color-text-muted)' }}>—</span>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
              {backedAgents.map(a => (
                <button key={a.id} className="reflink" onClick={() => select('agents', a.id)}>
                  {a.name || a.id}
                  <span className="reflink__arrow">→</span>
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 8, fontSize: 13, alignItems: 'baseline' }}>
      <span style={{ color: 'var(--color-text-muted)', minWidth: 72, flexShrink: 0 }}>{label}</span>
      <span>{children}</span>
    </div>
  )
}
