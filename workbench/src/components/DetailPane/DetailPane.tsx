import { useState, useEffect } from 'react'
import { useSelection } from '../../store/selectionStore'
import { YamlTab } from './YamlTab'
import { RefsTab } from './RefsTab'
import { LogsTab } from './LogsTab'
import { AgentRunsTab } from './AgentRunsTab'
import { AgentLogsTab } from './AgentLogsTab'
import { GatewayEventsTab } from './GatewayEventsTab'
import { McpEventsTab } from './McpEventsTab'
import { McpGatewayEventsTab } from './McpGatewayEventsTab'
import { AgentTestPanel } from '../TestPane/AgentTestPanel'
import { ProbeStub } from '../TestPane/ProbeStub'
import { usePlugins } from '../../plugins/usePlugins'
import { PluginTab } from '../../plugins/PluginTab'
import { PluginDetail } from './PluginDetail'
import { ExtensionDetail } from './ExtensionDetail'
import { GraphTab } from './GraphTab'

export function DetailPane() {
  const { kind, id } = useSelection()
  const [tab, setTab] = useState('yaml')
  const plugins = usePlugins()

  useEffect(() => {
    setTab('yaml')
  }, [kind, id])

  if (!kind || !id) {
    return (
      <div className="empty" style={{ flex: 1 }}>
        <div>
          <svg className="empty__icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <rect width="18" height="18" x="3" y="3" rx="2"/>
            <path d="M3 9h18"/><path d="M9 21V9"/>
          </svg>
          <div className="empty__title">Select a resource</div>
          <div className="empty__hint">Choose an item from the sidebar</div>
        </div>
      </div>
    )
  }

  if (kind === 'plugins') {
    return <PluginDetail pluginName={id} />
  }

  if (kind === 'extensions') {
    return <ExtensionDetail extensionId={id} />
  }

  const matchingPlugins = plugins.filter(p => p.kind === kind)

  const builtinTabs = [
    { key: 'yaml',  label: 'YAML',       isPlugin: false },
    ...(kind === 'graphs' ? [{ key: 'graph', label: 'Graph', isPlugin: false }] : []),
    { key: 'refs',  label: 'References', isPlugin: false },
    { key: 'test',  label: 'Test',       isPlugin: false },
    ...(kind === 'graphs' ? [{ key: 'logs', label: 'Logs', isPlugin: false }] : []),
    ...(kind === 'agents' ? [{ key: 'runs', label: 'Runs', isPlugin: false }] : []),
    ...(kind === 'agents' ? [{ key: 'agent-logs', label: 'Logs', isPlugin: false }] : []),
    ...(kind === 'llm-gateways' ? [{ key: 'events', label: 'Events', isPlugin: false }] : []),
    ...(kind === 'mcp-servers' ? [{ key: 'tool-logs', label: 'Tool Logs', isPlugin: false }] : []),
    ...(kind === 'mcp-gateways' ? [{ key: 'gw-tool-logs', label: 'Tool Logs', isPlugin: false }] : []),
  ]
  const pluginTabs = matchingPlugins.map(p => ({
    key: `plugin:${p.tabLabel}`,
    label: p.tabLabel,
    isPlugin: true,
  }))
  const allTabs = [...builtinTabs, ...pluginTabs]

  return (
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
      <nav className="tabs">
        {allTabs.map(t => (
          <div
            key={t.key}
            className={`tab${tab === t.key ? ' tab--active' : ''}`}
            onClick={() => setTab(t.key)}
          >
            {t.label}
            {t.isPlugin && <span className="tab__star">★</span>}
          </div>
        ))}
      </nav>

      <div style={{ flex: 1, minHeight: 0, overflow: tab === 'graph' ? 'hidden' : 'auto' }}>
        {tab === 'yaml' && <YamlTab kind={kind} id={id} />}
        {tab === 'graph' && <GraphTab id={id} />}
        {tab === 'refs' && <RefsTab kind={kind} id={id} />}
        {tab === 'logs' && <LogsTab id={id} />}
        {tab === 'runs' && <AgentRunsTab id={id} />}
        {tab === 'agent-logs' && <AgentLogsTab id={id} />}
        {tab === 'events' && <GatewayEventsTab id={id} />}
        {tab === 'tool-logs' && <McpEventsTab id={id} />}
        {tab === 'gw-tool-logs' && <McpGatewayEventsTab id={id} />}
        {tab === 'test' && (
          kind === 'agents' || kind === 'graphs'
            ? <AgentTestPanel kind={kind} id={id} />
            : <ProbeStub kind={kind} />
        )}
        {tab.startsWith('plugin:') && (() => {
          const label = tab.slice('plugin:'.length)
          const plugin = matchingPlugins.find(p => p.tabLabel === label)
          return plugin ? <PluginTab plugin={plugin} kind={kind} id={id} /> : null
        })()}
      </div>
    </div>
  )
}
