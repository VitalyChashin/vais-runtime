import { create } from 'zustand'
import type { SelectionKind } from '../api/types'

interface SelectionStore {
  kind: SelectionKind | null
  id: string | null
  select: (kind: SelectionKind, id: string) => void
  clear: () => void
}

export const useSelection = create<SelectionStore>(set => ({
  kind: null,
  id: null,
  select: (kind, id) => set({ kind, id }),
  clear: () => set({ kind: null, id: null }),
}))
