export interface Connection {
  name: string
  baseUrl: string
}

export interface WorkbenchConfig {
  connections: Connection[]
  activeConnection: string
}

export const DEFAULT_CONFIG: WorkbenchConfig = {
  connections: [{ name: 'localhost', baseUrl: 'http://localhost:8080' }],
  activeConnection: 'localhost',
}
