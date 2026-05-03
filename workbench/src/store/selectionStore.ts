import { create } from 'zustand'
import type { ResourceKind } from '../api/types'

interface SelectionStore {
  kind: ResourceKind | null
  id: string | null
  select: (kind: ResourceKind, id: string) => void
  clear: () => void
}

export const useSelection = create<SelectionStore>(set => ({
  kind: null,
  id: null,
  select: (kind, id) => set({ kind, id }),
  clear: () => set({ kind: null, id: null }),
}))
