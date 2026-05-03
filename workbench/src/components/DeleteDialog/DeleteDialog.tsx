import { useState } from 'react'
import { useQueryClient, useQuery } from '@tanstack/react-query'
import type { AgentManifest, ResourceKind } from '../../api/types'
import { useClient } from '../../api/useClient'
import { deleteResourceById, listAgents, listGraphs } from '../../api/resources'
import { useSelection } from '../../store/selectionStore'
import '../../styles/deleteDialog.css'

interface Props {
  kind: ResourceKind
  id: string
  onClose: () => void
}

function referencesId(agent: AgentManifest, targetId: string): boolean {
  return (
    agent.llmGatewayRef === targetId ||
    agent.mcpGatewayRef === targetId ||
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    ((agent as any).mcpServers?.includes(targetId) ?? false)
  )
}

export function DeleteDialog({ kind, id, onClose }: Props) {
  const client = useClient()
  const queryClient = useQueryClient()
  const { clear } = useSelection()
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState('')

  const { data: agents = [] } = useQuery({
    queryKey: ['agents', client.baseUrl],
    queryFn: () => listAgents(client),
  })
  const { data: graphs = [] } = useQuery({
    queryKey: ['graphs', client.baseUrl],
    queryFn: () => listGraphs(client),
  })

  const reverseRefs: string[] = [
    ...agents.filter(a => referencesId(a, id)).map(a => a.name || a.id),
    ...graphs
      .filter(g => (g as AgentManifest).llmGatewayRef === id || (g as AgentManifest).mcpGatewayRef === id)
      .map(g => g.name || g.id),
  ]

  async function handleDelete() {
    setDeleting(true)
    setError('')
    try {
      await deleteResourceById(client, kind, id)
      await queryClient.invalidateQueries({ queryKey: [kind] })
      clear()
      onClose()
    } catch (e) {
      setDeleting(false)
      setError((e as Error).message)
    }
  }

  return (
    <div className="overlay">
      <div className="modal" style={{ width: 480 }}>
        <div className="modal__header">
          <div className="modal__title">Delete resource</div>
          <button className="modal__close" onClick={onClose} aria-label="Close" disabled={deleting}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M18 6 6 18"/><path d="m6 6 12 12"/>
            </svg>
          </button>
        </div>

        <div className="delete-body">
          <p className="delete-prompt">
            Delete <strong>{id}</strong> ({kind})? This action cannot be undone.
          </p>

          {reverseRefs.length > 0 && (
            <div className="warn-callout">
              <svg className="warn-callout__icon" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"/>
                <path d="M12 9v4"/><path d="M12 17h.01"/>
              </svg>
              <div>
                <div className="warn-callout__title">References will break</div>
                These resources reference this and may fail at next activation:
                <ul className="warn-callout__list">
                  {reverseRefs.map(name => (
                    <li key={name}>{name}</li>
                  ))}
                </ul>
              </div>
            </div>
          )}

          {error && <p className="delete-error">{error}</p>}
        </div>

        <div className="modal__footer">
          <div className="modal__footer-spacer" />
          <button className="btn btn--ghost" onClick={onClose} disabled={deleting}>
            Cancel
          </button>
          <button className="btn btn--danger" onClick={handleDelete} disabled={deleting}>
            {deleting ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </div>
    </div>
  )
}
