import { useQuery } from '@tanstack/react-query'
import type { ResourceKind } from '../api/types'
import type { VaisClient } from '../api/client'
import { useClient } from '../api/useClient'

export function useResourceList<T extends { id: string }>(
  kind: ResourceKind,
  fetcher: (client: VaisClient) => Promise<T[]>,
): { data: T[]; isLoading: boolean; error: Error | null } {
  const client = useClient()
  const result = useQuery({
    queryKey: [kind, client.baseUrl],
    queryFn: () => fetcher(client),
    refetchInterval: 5000,
  })
  return {
    data: Array.isArray(result.data) ? result.data : [],
    isLoading: result.isLoading,
    error: result.error as Error | null,
  }
}
