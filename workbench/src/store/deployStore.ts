import { create } from 'zustand'

interface DeployStore {
  open: boolean
  initialYaml: string
  openDeploy: (yaml: string) => void
  closeDeploy: () => void
}

export const useDeployStore = create<DeployStore>(set => ({
  open: false,
  initialYaml: '',
  openDeploy: (yaml) => set({ open: true, initialYaml: yaml }),
  closeDeploy: () => set({ open: false, initialYaml: '' }),
}))
