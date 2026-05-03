import { useState } from 'react'
import Editor from '@monaco-editor/react'
import { useQuery } from '@tanstack/react-query'
import * as yaml from 'js-yaml'
import type { ResourceKind } from '../../api/types'
import { useClient } from '../../api/useClient'
import { getResource } from '../../api/resources'
import { useDeployStore } from '../../store/deployStore'
import { DeleteDialog } from '../DeleteDialog/DeleteDialog'
import { vaisDarkTheme } from '../../monacoTheme'

interface Props {
  kind: ResourceKind
  id: string
}

const KIND_LABEL: Record<ResourceKind, string> = {
  'agents':       'Agent',
  'graphs':       'Graph',
  'llm-gateways': 'LLM Gateway',
  'mcp-gateways': 'MCP Gateway',
  'mcp-servers':  'MCP Server',
}

function defineTheme(monaco: { editor: { defineTheme: (name: string, data: typeof vaisDarkTheme) => void } }) {
  monaco.editor.defineTheme('vais-dark', vaisDarkTheme)
}

export function YamlTab({ kind, id }: Props) {
  const client = useClient()
  const { openDeploy } = useDeployStore()
  const [confirmDelete, setConfirmDelete] = useState(false)

  const { data, isLoading, error } = useQuery({
    queryKey: [kind, id, client.baseUrl],
    queryFn: () => getResource(client, kind, id),
  })

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
        Failed to load resource
      </div>
    )
  }

  const yamlContent = yaml.dump(data)
  const displayName = (data as { name?: string }).name || id

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div className="toolbar">
        <div className="toolbar__title">
          <span>{displayName}</span>
          <span className="toolbar__sep">·</span>
          <span className="kind">{KIND_LABEL[kind]}</span>
        </div>
        <div className="toolbar__spacer" />
        <div className="toolbar__actions">
          <button className="btn btn--ghost" onClick={() => openDeploy(yamlContent)}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z"/>
              <path d="m15 5 4 4"/>
            </svg>
            Edit
          </button>
          <button
            className="btn btn--bare"
            title="Delete"
            onClick={() => setConfirmDelete(true)}
            style={{ color: 'var(--color-text-muted)' }}
            onMouseEnter={e => (e.currentTarget.style.color = 'var(--color-error)')}
            onMouseLeave={e => (e.currentTarget.style.color = 'var(--color-text-muted)')}
          >
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 6h18"/>
              <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/>
              <path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
              <line x1="10" x2="10" y1="11" y2="17"/>
              <line x1="14" x2="14" y1="11" y2="17"/>
            </svg>
            Delete
          </button>
        </div>
      </div>

      <div style={{ flex: 1, minHeight: 0 }}>
        <Editor
          height="100%"
          language="yaml"
          value={yamlContent}
          beforeMount={defineTheme}
          theme="vais-dark"
          options={{
            readOnly: true,
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            fontSize: 13,
          }}
        />
      </div>

      {confirmDelete && (
        <DeleteDialog kind={kind} id={id} onClose={() => setConfirmDelete(false)} />
      )}
    </div>
  )
}
