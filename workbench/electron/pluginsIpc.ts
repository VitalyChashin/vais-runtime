import { ipcMain } from 'electron'
import * as fs from 'fs'
import * as path from 'path'
import * as os from 'os'

export function registerPluginsIpc() {
  ipcMain.handle('plugins:load', () => {
    const pluginsDir = path.join(os.homedir(), '.vais', 'workbench', 'plugins')
    fs.mkdirSync(pluginsDir, { recursive: true })

    const files = fs.readdirSync(pluginsDir).filter(f => f.endsWith('.js'))
    const result: Array<{ kind: string; tabLabel: string; renderSource: string }> = []

    for (const file of files) {
      const fullPath = path.join(pluginsDir, file)
      try {
        delete require.cache[require.resolve(fullPath)]
        const mod = require(fullPath) as unknown
        if (
          mod !== null &&
          typeof mod === 'object' &&
          typeof (mod as Record<string, unknown>).kind === 'string' &&
          typeof (mod as Record<string, unknown>).tabLabel === 'string' &&
          typeof (mod as Record<string, unknown>).render === 'function'
        ) {
          const m = mod as { kind: string; tabLabel: string; render: Function }
          result.push({ kind: m.kind, tabLabel: m.tabLabel, renderSource: m.render.toString() })
        }
      } catch {
        // skip invalid or erroring plugins
      }
    }

    return result
  })
}
