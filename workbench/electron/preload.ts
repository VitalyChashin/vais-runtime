import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('vais', {
  readConfig: () => ipcRenderer.invoke('config:read'),
  writeConfig: (config: unknown) => ipcRenderer.invoke('config:write', config),
  loadPlugins: () => ipcRenderer.invoke('plugins:load'),
})
