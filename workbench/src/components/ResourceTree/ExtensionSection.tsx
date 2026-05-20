// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

import { useState } from 'react'
import type { ExtensionInfo } from '../../api/types'
import { useSelection } from '../../store/selectionStore'

interface Props {
  data: ExtensionInfo[]
  isLoading: boolean
  error: Error | null
}

const SECTION_ICON = (
  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M9 3H5a2 2 0 0 0-2 2v4m6-6h10a2 2 0 0 1 2 2v4M9 3v18m0 0h10a2 2 0 0 0 2-2V9M9 21H5a2 2 0 0 1-2-2V9m0 0h18"/>
  </svg>
)

const ROW_ICON = (
  <svg className="row__icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M9 3H5a2 2 0 0 0-2 2v4m6-6h10a2 2 0 0 1 2 2v4M9 3v18m0 0h10a2 2 0 0 0 2-2V9M9 21H5a2 2 0 0 1-2-2V9m0 0h18"/>
  </svg>
)

const SKELETON_WIDTHS = [88, 64, 104]

export function ExtensionSection({ data, isLoading, error }: Props) {
  const [open, setOpen] = useState(true)
  const { kind: selectedKind, id: selectedId, select } = useSelection()

  return (
    <div className={`section${open ? '' : ' section--collapsed'}`}>
      <div className="section__head" onClick={() => setOpen(o => !o)}>
        <svg className="section__chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="6 9 12 15 18 9"/>
        </svg>
        {SECTION_ICON}
        <span className="section__title">Extensions</span>
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
        {!isLoading && !error && data.map(ext => (
          <div
            key={ext.extensionId}
            className={`row${selectedKind === 'extensions' && selectedId === ext.extensionId ? ' row--selected' : ''}`}
            title={`${ext.extensionId} v${ext.version} (${ext.host})`}
            onClick={() => select('extensions', ext.extensionId)}
          >
            {ROW_ICON}
            <span className="row__name">{ext.extensionId}</span>
            <span style={{ fontSize: 9, color: 'var(--color-text-muted)', marginLeft: 4 }}>
              {ext.host === 'csharp' ? 'C#' : ext.host === 'container' ? 'ctr' : ext.host}
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}
