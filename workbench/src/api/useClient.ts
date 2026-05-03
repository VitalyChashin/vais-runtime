import { useMemo } from 'react'
import { useConfig } from '../config/useConfig'
import { VaisClient } from './client'

export function useClient(): VaisClient {
  const { config, activeConnection } = useConfig()

  const baseUrl = useMemo(() => {
    if (!config) return 'http://localhost:8080'
    return config.connections.find(c => c.name === activeConnection)?.baseUrl ?? 'http://localhost:8080'
  }, [config, activeConnection])

  return useMemo(() => new VaisClient(baseUrl), [baseUrl])
}
