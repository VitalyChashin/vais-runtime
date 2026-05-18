export {}

declare global {
  interface Window {
    vais: {
      readConfig(): Promise<import('./config/types').WorkbenchConfig>
      writeConfig(config: import('./config/types').WorkbenchConfig): Promise<void>
      loadPlugins(): Promise<Array<{ kind: string; tabLabel: string; renderSource: string }>>
      pushPluginSource(
        name: string,
        baseUrl: string,
      ): Promise<
        | { cancelled: true }
        | { cancelled?: false; pluginName: string; status: string; processId?: number | null; errorMessage?: string | null }
      >
      pushPluginDll(
        name: string,
        baseUrl: string,
      ): Promise<
        | { cancelled: true }
        | { cancelled?: false; pluginName: string; status: string; handlers?: string[] | null; targetApiVersion?: string | null; errorMessage?: string | null }
      >
    }
  }
}
