import { useConfig } from '../../config/useConfig'

export function ConnectionBar() {
  const { config, activeConnection, setActiveConnection } = useConfig()

  return (
    <div className="connbar">
      <span className="connbar__status" title="Connected" />
      <span>Connection</span>
      <select
        className="connbar__select"
        aria-label="Active connection"
        value={activeConnection ?? ''}
        onChange={e => setActiveConnection(e.target.value)}
        disabled={!config}
      >
        {config?.connections.map(c => (
          <option key={c.name} value={c.name}>{c.name}</option>
        ))}
      </select>
    </div>
  )
}
