export {}

declare global {
  interface Window {
    vais: {
      readConfig(): Promise<import('./config/types').WorkbenchConfig>
      writeConfig(config: import('./config/types').WorkbenchConfig): Promise<void>
      loadPlugins(): Promise<Array<{ kind: string; tabLabel: string; renderSource: string }>>
    }
  }
}
