import { useState } from 'react'
import type { ResourceKind } from '../../api/types'
import { useSelection } from '../../store/selectionStore'

interface Props {
  kind: ResourceKind
  label: string
  data: Array<{ id: string; name: string }>
  isLoading: boolean
  error: Error | null
}

const SECTION_ICONS = {
  agents: (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 8V4H8"/>
      <rect width="16" height="12" x="4" y="8" rx="2"/>
      <path d="M2 14h2"/><path d="M20 14h2"/>
      <path d="M15 13v2"/><path d="M9 13v2"/>
    </svg>
  ),
  graphs: (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <line x1="6" x2="6" y1="3" y2="15"/>
      <circle cx="18" cy="6" r="3"/>
      <circle cx="6" cy="18" r="3"/>
      <path d="M18 9a9 9 0 0 1-9 9"/>
    </svg>
  ),
  'llm-gateways': (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect width="16" height="16" x="4" y="4" rx="2"/>
      <rect width="6" height="6" x="9" y="9"/>
      <path d="M15 2v2"/><path d="M15 20v2"/>
      <path d="M2 15h2"/><path d="M2 9h2"/>
      <path d="M20 15h2"/><path d="M20 9h2"/>
      <path d="M9 2v2"/><path d="M9 20v2"/>
    </svg>
  ),
  'mcp-gateways': (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect width="20" height="8" x="2" y="2" rx="2"/>
      <rect width="20" height="8" x="2" y="14" rx="2"/>
      <line x1="6" x2="6.01" y1="6" y2="6"/>
      <line x1="6" x2="6.01" y1="18" y2="18"/>
    </svg>
  ),
  'mcp-servers': (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect width="20" height="8" x="2" y="2" rx="2"/>
      <rect width="20" height="8" x="2" y="14" rx="2"/>
      <line x1="6" x2="6.01" y1="6" y2="6"/>
      <line x1="6" x2="6.01" y1="18" y2="18"/>
    </svg>
  ),
}

const ROW_ICON = (
  <svg className="row__icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <circle cx="12" cy="12" r="2"/>
    <path d="M12 2v2"/><path d="M12 20v2"/>
    <path d="m4.93 4.93 1.41 1.41"/>
    <path d="m17.66 17.66 1.41 1.41"/>
  </svg>
)

const SKELETON_WIDTHS = [96, 72, 110]

export function ResourceSection({ kind, label, data, isLoading, error }: Props) {
  const [open, setOpen] = useState(true)
  const { kind: selectedKind, id: selectedId, select } = useSelection()

  return (
    <div className={`section${open ? '' : ' section--collapsed'}`}>
      <div className="section__head" onClick={() => setOpen(o => !o)}>
        <svg className="section__chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="6 9 12 15 18 9"/>
        </svg>
        {SECTION_ICONS[kind]}
        <span className="section__title">{label}</span>
        {!isLoading && (
          <span className={`section__count${data.length === 0 ? ' section__count--zero' : ''}`}>
            {data.length}
          </span>
        )}
      </div>

      <div className="section__body">
        {isLoading && SKELETON_WIDTHS.map((w, i) => (
          <div key={i} className="row row--skeleton">
            <div className="skel" style={{ width: w }} />
          </div>
        ))}
        {!isLoading && error && (
          <div className="row row--muted">Failed to load</div>
        )}
        {!isLoading && !error && data.length === 0 && (
          <div className="row row--muted">None deployed</div>
        )}
        {!isLoading && !error && data.map(item => (
          <div
            key={item.id}
            className={`row${selectedKind === kind && selectedId === item.id ? ' row--selected' : ''}`}
            title={item.name || item.id}
            onClick={() => select(kind, item.id)}
          >
            {ROW_ICON}
            <span className="row__name">{item.name || item.id}</span>
          </div>
        ))}
      </div>
    </div>
  )
}
