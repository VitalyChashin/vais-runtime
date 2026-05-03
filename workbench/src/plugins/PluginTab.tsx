import { useQuery } from '@tanstack/react-query'
import type { ResourceKind } from '../api/types'
import type { PanelPlugin } from './types'
import { useClient } from '../api/useClient'
import { getResource } from '../api/resources'

interface Props {
  plugin: PanelPlugin
  kind: ResourceKind
  id: string
}

export function PluginTab({ plugin, kind, id }: Props) {
  const client = useClient()
  const { data, isLoading, error } = useQuery({
    queryKey: [kind, id, client.baseUrl],
    queryFn: () => getResource(client, kind, id),
  })

  if (isLoading) return <div className="p-4 text-sm text-gray-400">Loading…</div>
  if (error || !data) return <div className="p-4 text-sm text-red-500">Failed to load resource</div>

  let html = ''
  try {
    html = plugin.render(data)
  } catch (e) {
    return <div className="p-4 text-sm text-red-500">Plugin error: {(e as Error).message}</div>
  }

  // intentionally unsafe: in-process, local files only (v1 scope — see research doc §6.7)
  return <div className="p-4" dangerouslySetInnerHTML={{ __html: html }} />
}
