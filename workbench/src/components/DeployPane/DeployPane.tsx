import { useState, useCallback } from 'react'
import Editor from '@monaco-editor/react'
import * as yaml from 'js-yaml'
import { useQueryClient } from '@tanstack/react-query'
import type { ResourceKind } from '../../api/types'
import { useClient } from '../../api/useClient'
import { createResource, validateResource } from '../../api/resources'
import { useDeployStore } from '../../store/deployStore'
import { vaisDarkTheme } from '../../monacoTheme'

const KIND_ORDER: ResourceKind[] = ['llm-gateways', 'mcp-gateways', 'mcp-servers', 'agents', 'graphs']

function detectKind(doc: Record<string, unknown>): ResourceKind | null {
  const k = typeof doc.kind === 'string' ? doc.kind.toLowerCase() : ''
  if (k === 'agent') return 'agents'
  if (k === 'agentgraph' || k === 'graph') return 'graphs'
  if (k === 'llmgateway' || k === 'llm-gateway') return 'llm-gateways'
  if (k === 'mcpgateway' || k === 'mcp-gateway') return 'mcp-gateways'
  if (k === 'mcpserver' || k === 'mcp-server') return 'mcp-servers'
  return null
}

function defineTheme(monaco: { editor: { defineTheme: (name: string, data: typeof vaisDarkTheme) => void } }) {
  monaco.editor.defineTheme('vais-dark', vaisDarkTheme)
}

type Status = 'idle' | 'validating' | 'applying' | 'done' | 'error'

export function DeployPane() {
  const { initialYaml, closeDeploy } = useDeployStore()
  const client = useClient()
  const queryClient = useQueryClient()

  const [editorValue, setEditorValue] = useState(initialYaml)
  const [status, setStatus] = useState<Status>('idle')
  const [message, setMessage] = useState('')

  const parseDocs = useCallback((): Array<{ kind: ResourceKind; doc: Record<string, unknown> }> | null => {
    try {
      const docs = yaml.loadAll(editorValue) as Array<Record<string, unknown>>
      const result: Array<{ kind: ResourceKind; doc: Record<string, unknown> }> = []
      for (const doc of docs) {
        if (!doc || typeof doc !== 'object') continue
        const kind = detectKind(doc)
        if (!kind) {
          setMessage(`Unknown kind: ${String(doc.kind ?? '(missing)')}`)
          return null
        }
        result.push({ kind, doc })
      }
      return result
    } catch (e) {
      setMessage(`YAML parse error: ${(e as Error).message}`)
      return null
    }
  }, [editorValue])

  async function handleValidate() {
    setStatus('validating')
    setMessage('')
    const docs = parseDocs()
    if (!docs) { setStatus('error'); return }
    try {
      const result = await validateResource(client, docs[0].kind, docs[0].doc)
      if (result.valid) {
        setStatus('idle')
        setMessage('Valid')
      } else {
        setStatus('error')
        setMessage(result.errors?.join('\n') ?? 'Invalid')
      }
    } catch (e) {
      setStatus('error')
      setMessage((e as Error).message)
    }
  }

  async function handleApply() {
    setStatus('applying')
    setMessage('')
    const docs = parseDocs()
    if (!docs) { setStatus('error'); return }

    const sorted = [...docs].sort(
      (a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind)
    )

    try {
      for (const { kind, doc } of sorted) {
        await createResource(client, kind, doc)
      }
      for (const { kind } of sorted) {
        await queryClient.invalidateQueries({ queryKey: [kind] })
      }
      setStatus('done')
      setMessage(`Applied ${sorted.length} resource(s)`)
    } catch (e) {
      setStatus('error')
      setMessage((e as Error).message)
    }
  }

  return (
    <div className="overlay">
      <div className="modal" style={{ width: 760, height: 560 }}>
        <div className="modal__header">
          <div className="modal__title">Deploy resource</div>
          <button className="modal__close" onClick={closeDeploy} aria-label="Close">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M18 6 6 18"/><path d="m6 6 12 12"/>
            </svg>
          </button>
        </div>

        <div className="modal__body">
          <Editor
            defaultValue={initialYaml}
            language="yaml"
            beforeMount={defineTheme}
            theme="vais-dark"
            options={{ minimap: { enabled: false }, fontSize: 13, scrollBeyondLastLine: false }}
            onChange={v => setEditorValue(v ?? '')}
          />
        </div>

        <div className="modal__footer">
          {message && (
            <span className={`vmsg ${status === 'error' ? 'vmsg--err' : 'vmsg--ok'}`}>
              {status === 'error' ? (
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <circle cx="12" cy="12" r="10"/><line x1="15" x2="9" y1="9" y2="15"/><line x1="9" x2="15" y1="9" y2="15"/>
                </svg>
              ) : (
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/>
                </svg>
              )}
              {message}
            </span>
          )}
          <div className="modal__footer-spacer" />
          <button
            className="btn btn--ghost"
            onClick={handleValidate}
            disabled={status === 'validating' || status === 'applying'}
          >
            Validate
          </button>
          <button
            className="btn btn--primary"
            onClick={handleApply}
            disabled={status === 'applying'}
          >
            {status === 'applying' ? 'Applying…' : 'Apply'}
          </button>
        </div>
      </div>
    </div>
  )
}
