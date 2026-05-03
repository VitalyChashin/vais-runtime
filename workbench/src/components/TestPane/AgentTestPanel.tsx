import { useState, useRef } from 'react'
import { useClient } from '../../api/useClient'
import { invokeResource } from '../../api/resources'

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
      const response = await invokeResource(client, kind, id, { text: input })
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
    <div className="flex flex-col h-full p-4 gap-4">
      <div className="flex gap-2 items-end">
        <textarea
          className="flex-1 border rounded p-2 text-sm resize-none h-20 focus:outline-none focus:ring-1 focus:ring-blue-500"
          placeholder="Enter message… (Shift+Enter for newline)"
          value={message}
          onChange={e => setMessage(e.target.value)}
          onKeyDown={e => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleSend() }
          }}
        />
        <button
          onClick={handleSend}
          disabled={sending || !message.trim()}
          className="px-4 py-2 bg-blue-600 text-white text-sm rounded hover:bg-blue-700 disabled:opacity-50"
        >
          {sending ? 'Sending…' : 'Send'}
        </button>
      </div>

      <div className="flex flex-col gap-3 overflow-auto flex-1">
        {runs.length === 0 ? (
          <p className="text-sm text-gray-400 text-center mt-8">No runs yet.</p>
        ) : (
          runs.map(run => (
            <div key={run.id} className="border rounded p-3 text-sm flex flex-col gap-2">
              <div>
                <span className="text-xs font-semibold text-gray-500 uppercase tracking-wide">Input</span>
                <p className="mt-1 whitespace-pre-wrap">{run.input}</p>
              </div>
              <div>
                <span className="text-xs font-semibold text-gray-500 uppercase tracking-wide">Output</span>
                {run.error ? (
                  <p className="mt-1 text-red-600">{run.error}</p>
                ) : (
                  <pre className="mt-1 whitespace-pre-wrap font-mono text-xs bg-gray-50 rounded p-2">
                    {run.chunks.join('')}
                    {!run.done && (
                      <span className="inline-block w-1.5 h-3.5 bg-gray-400 animate-pulse align-middle ml-0.5" />
                    )}
                  </pre>
                )}
              </div>
            </div>
          ))
        )}
      </div>
    </div>
  )
}
