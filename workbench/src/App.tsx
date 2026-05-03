import { ConnectionBar } from './components/ConnectionBar/ConnectionBar'
import { ResourceTree } from './components/ResourceTree/ResourceTree'
import { DetailPane } from './components/DetailPane/DetailPane'
import { DeployPane } from './components/DeployPane/DeployPane'
import { useDeployStore } from './store/deployStore'
import { useConfig } from './config/useConfig'

function SidebarFooter() {
  const { config, activeConnection } = useConfig()
  const conn = config?.connections.find(c => c.name === activeConnection)
  return (
    <div className="sidebar__footer">
      <span>{conn?.baseUrl ?? '—'}</span>
      <span style={{ color: 'var(--color-success)' }}>● connected</span>
    </div>
  )
}

export default function App() {
  const { open, openDeploy } = useDeployStore()

  return (
    <div className="app">
      <header className="header">
        <div className="header__logo" aria-label="Vais Workbench">
          <span className="header__logo-mark">V</span>
          <span>VAIS</span>
        </div>
        <span className="header__divider" />
        <ConnectionBar />
        <div className="header__spacer" />
        <button className="btn btn--primary" onClick={() => openDeploy('')}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
            <polyline points="17 8 12 3 7 8"/>
            <line x1="12" x2="12" y1="3" y2="15"/>
          </svg>
          Deploy
        </button>
      </header>

      <aside className="sidebar">
        <div className="sidebar__scroll">
          <ResourceTree />
        </div>
        <SidebarFooter />
      </aside>

      <main className="main">
        <DetailPane />
      </main>

      {open && <DeployPane />}
    </div>
  )
}
