// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

import { useState } from 'react'
import type { PluginInfo } from '../../api/types'
import { useSelection } from '../../store/selectionStore'

interface Props {
  data: PluginInfo[]
  isLoading: boolean
  error: Error | null
}

const SECTION_ICON = (
  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 2L2 7l10 5 10-5-10-5z"/>
    <path d="M2 17l10 5 10-5"/>
    <path d="M2 12l10 5 10-5"/>
  </svg>
)

const ROW_ICON = (
  <svg className="row__icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <circle cx="12" cy="12" r="2"/>
    <path d="M12 2v2"/><path d="M12 20v2"/>
    <path d="m4.93 4.93 1.41 1.41"/>
    <path d="m17.66 17.66 1.41 1.41"/>
  </svg>
)

const STATE_COLOR: Record<string, string> = {
  Ready: 'var(--color-success)',
  Loading: 'var(--color-warn)',
  Restarting: 'var(--color-warn)',
  Unavailable: 'var(--color-error)',
}

const SKELETON_WIDTHS = [96, 72, 110]

export function PluginSection({ data, isLoading, error }: Props) {
  const [open, setOpen] = useState(true)
  const { kind: selectedKind, id: selectedId, select } = useSelection()

  return (
    <div className={`section${open ? '' : ' section--collapsed'}`}>
      <div className="section__head" onClick={() => setOpen(o => !o)}>
        <svg className="section__chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="6 9 12 15 18 9"/>
        </svg>
        {SECTION_ICON}
        <span className="section__title">Plugins</span>
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
          <div className="row row--muted">None loaded</div>
        )}
        {!isLoading && !error && data.map(plugin => (
          <div
            key={plugin.name}
            className={`row${selectedKind === 'plugins' && selectedId === plugin.name ? ' row--selected' : ''}`}
            title={`${plugin.name} (${plugin.kind})`}
            onClick={() => select('plugins', plugin.name)}
          >
            {ROW_ICON}
            <span className="row__name">{plugin.name}</span>
            <span style={{ marginLeft: 'auto', fontSize: 10, color: STATE_COLOR[plugin.state] ?? 'currentColor' }}>●</span>
          </div>
        ))}
      </div>
    </div>
  )
}
