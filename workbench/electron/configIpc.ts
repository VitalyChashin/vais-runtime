import { ipcMain } from 'electron'
import * as fs from 'fs'
import * as path from 'path'
import * as os from 'os'
import * as yaml from 'js-yaml'
import { DEFAULT_CONFIG } from '../src/config/types'
import type { WorkbenchConfig } from '../src/config/types'

const configDir = path.join(os.homedir(), '.vais', 'workbench')
const configPath = path.join(configDir, 'config.yaml')

export function registerConfigIpc() {
  ipcMain.handle('config:read', (): WorkbenchConfig => {
    if (!fs.existsSync(configDir)) {
      fs.mkdirSync(configDir, { recursive: true })
    }
    if (!fs.existsSync(configPath)) {
      fs.writeFileSync(configPath, yaml.dump(DEFAULT_CONFIG), 'utf-8')
      return DEFAULT_CONFIG
    }
    return yaml.load(fs.readFileSync(configPath, 'utf-8')) as WorkbenchConfig
  })

  ipcMain.handle('config:write', (_event, config: WorkbenchConfig) => {
    if (!fs.existsSync(configDir)) {
      fs.mkdirSync(configDir, { recursive: true })
    }
    fs.writeFileSync(configPath, yaml.dump(config), 'utf-8')
  })
}
