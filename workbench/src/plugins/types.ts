export interface PanelPlugin {
  kind: string
  tabLabel: string
  render: (resource: unknown) => string
}
