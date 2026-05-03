import { useState, useRef } from 'react'
import { useClient } from '../../api/useClient'
import { invokeResource } from '../../api/resources'
import '../../styles/testPanel.css'

interface Run {
  id: number
  input: string
  chunks: string[]
  done: boolean
  error?: string
}

interface Props {
  kind: 'agents' | 'graphs'
  id: string
}

export function AgentTestPanel({ kind, id }: Props) {
  const client = useClient()
  const [message, setMessage] = useState('')
  const [runs, setRuns] = useState<Run[]>([])
  const [sending, setSending] = useState(false)
  const counter = useRef(0)

  async function handleSend() {
    if (!message.trim() || sending) return
    const runId = ++counter.current
    const input = message.trim()
    setMessage('')
    setSending(true)
    setRuns(prev => [{ id: runId, input, chunks: [], done: false }, ...prev].slice(0, 5))

    try {
      const body = kind === 'graphs'
        ? { initialState: { query: input } }
        : { text: input }
      const response = await invokeResource(client, kind, id, body)
      const reader = response.body?.getReader()

      if (!reader) {
        const text = await response.text()
        setRuns(prev => prev.map(r => r.id === runId ? { ...r, chunks: [text], done: true } : r))
      } else {
        const decoder = new TextDecoder()
        while (true) {
          const { value, done } = await reader.read()
          if (done) break
          const chunk = decoder.decode(value, { stream: true })
          setRuns(prev => prev.map(r =>
            r.id === runId ? { ...r, chunks: [...r.chunks, chunk] } : r
          ))
        }
        setRuns(prev => prev.map(r => r.id === runId ? { ...r, done: true } : r))
      }
    } catch (e) {
      setRuns(prev => prev.map(r =>
        r.id === runId ? { ...r, done: true, error: (e as Error).message } : r
      ))
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="test">
      <div className="test__input">
        <textarea
          className="test__textarea"
          placeholder="Enter message… (Shift+Enter for newline)"
          value={message}
          onChange={e => setMessage(e.target.value)}
          onKeyDown={e => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleSend() }
          }}
        />
        <div className="test__send-wrap">
          <button
            className="btn btn--primary"
            style={{ flex: 1 }}
            onClick={handleSend}
            disabled={sending || !message.trim()}
          >
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
              <polygon points="6 3 20 12 6 21 6 3"/>
            </svg>
            {sending ? 'Sending…' : 'Send'}
          </button>
          <span className="test__send-hint">Shift+↵ newline</span>
        </div>
      </div>

      {runs.length === 0 ? (
        <div className="test__empty">No runs yet.</div>
      ) : (
        <div className="test__runs">
          {runs.map(run => (
            <div key={run.id} className="run">
              <div className="run__hd">Input</div>
              <div className="run__input">{run.input}</div>
              <div className="run__divider" />
              <div className="run__hd">Output</div>
              {run.error ? (
                <div className="run__error">{run.error}</div>
              ) : (
                <div className="run__output">
                  {run.chunks.join('')}
                  {!run.done && <span className="run__cursor" />}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
