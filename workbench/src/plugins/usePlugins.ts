import { useState, useEffect } from 'react'
import type { PanelPlugin } from './types'

export function usePlugins(): PanelPlugin[] {
  const [plugins, setPlugins] = useState<PanelPlugin[]>([])

  useEffect(() => {
    const vais = (window as { vais?: Window['vais'] }).vais
    if (!vais) return

    vais.loadPlugins().then(entries => {
      const loaded: PanelPlugin[] = entries.map(({ kind, tabLabel, renderSource }) => ({
        kind,
        tabLabel,
        render: new Function(`return (${renderSource})`)() as (resource: unknown) => string,
      }))
      setPlugins(loaded)
    }).catch(() => { /* silently ignore plugin load errors */ })
  }, [])

  return plugins
}
