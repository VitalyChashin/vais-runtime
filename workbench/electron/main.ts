import { app, BrowserWindow } from 'electron'
import * as path from 'path'
import { registerConfigIpc } from './configIpc'
import { registerPluginsIpc } from './pluginsIpc'

function createWindow() {
  const win = new BrowserWindow({
    width: 1280,
    height: 800,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  })

  if (process.env.VITE_DEV_SERVER_URL) {
    win.loadURL(process.env.VITE_DEV_SERVER_URL)
  } else {
    win.loadFile(path.join(__dirname, '../dist/index.html'))
  }
}

registerConfigIpc()
registerPluginsIpc()
app.whenReady().then(createWindow)
