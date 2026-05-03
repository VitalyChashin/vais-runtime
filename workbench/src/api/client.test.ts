import { describe, it, expect, vi, beforeEach } from 'vitest'
import { VaisClient, ApiError } from './client'

const BASE = 'http://localhost:5000'

describe('VaisClient', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('GET hits the correct endpoint', async () => {
    const mockFetch = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve([{ id: '1' }]),
    })
    vi.stubGlobal('fetch', mockFetch)

    const client = new VaisClient(BASE)
    await client.get('/v1/agents')

    expect(mockFetch).toHaveBeenCalledWith(`${BASE}/v1/agents`)
  })

  it('POST sends JSON body to correct endpoint', async () => {
    const mockFetch = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ id: '1' }),
    })
    vi.stubGlobal('fetch', mockFetch)

    const client = new VaisClient(BASE)
    await client.post('/v1/agents', { name: 'test' })

    expect(mockFetch).toHaveBeenCalledWith(`${BASE}/v1/agents`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'test' }),
    })
  })

  it('DELETE sends DELETE method to correct endpoint', async () => {
    const mockFetch = vi.fn().mockResolvedValue({ ok: true })
    vi.stubGlobal('fetch', mockFetch)

    const client = new VaisClient(BASE)
    await client.delete('/v1/agents/1')

    expect(mockFetch).toHaveBeenCalledWith(`${BASE}/v1/agents/1`, { method: 'DELETE' })
  })

  it('GET throws ApiError on non-ok response', async () => {
    const mockFetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 404,
      text: () => Promise.resolve('Not Found'),
    })
    vi.stubGlobal('fetch', mockFetch)

    const client = new VaisClient(BASE)
    await expect(client.get('/v1/agents/missing')).rejects.toBeInstanceOf(ApiError)
  })

  it('POST throws ApiError with status on non-ok response', async () => {
    const mockFetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 400,
      text: () => Promise.resolve('Bad Request'),
    })
    vi.stubGlobal('fetch', mockFetch)

    const client = new VaisClient(BASE)
    const err = await client.post('/v1/agents', {}).catch(e => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(400)
  })
})
