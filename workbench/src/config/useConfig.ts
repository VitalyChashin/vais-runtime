import { useState, useEffect } from 'react'
import type { WorkbenchConfig } from './types'

export function useConfig() {
  const [config, setConfig] = useState<WorkbenchConfig | null>(null)

  useEffect(() => {
    window.vais.readConfig().then(setConfig)
  }, [])

  const setActiveConnection = async (name: string) => {
    if (!config) return
    const next = { ...config, activeConnection: name }
    await window.vais.writeConfig(next)
    setConfig(next)
  }

  return {
    config,
    activeConnection: config?.activeConnection ?? null,
    setActiveConnection,
  }
}
