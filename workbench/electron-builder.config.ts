import type { Configuration } from 'electron-builder'

const config: Configuration = {
  appId: 'ai.vais.workbench',
  productName: 'Vais Workbench',
  directories: { output: 'release' },
  files: ['dist', 'dist-electron'],
  win: { target: 'nsis' },
  mac: { target: 'dmg' },
  linux: { target: 'AppImage' },
}

export default config
