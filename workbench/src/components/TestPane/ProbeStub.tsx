import type { ResourceKind } from '../../api/types'

interface Props {
  kind: ResourceKind
}

export function ProbeStub({ kind }: Props) {
  return (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      height: '100%',
      gap: 12,
      padding: 24,
    }}>
      <button className="btn btn--ghost" disabled style={{ opacity: 0.5, cursor: 'not-allowed' }}>
        Probe
      </button>
      <p style={{
        fontSize: 12,
        color: 'var(--color-text-muted)',
        textAlign: 'center',
        maxWidth: 280,
        lineHeight: 1.6,
        margin: 0,
      }}>
        Probe endpoint for <code style={{ fontFamily: 'ui-monospace, monospace' }}>{kind}</code> is not yet available.
      </p>
    </div>
  )
}
