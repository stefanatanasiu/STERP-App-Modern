import { useSession, type RequestContext } from '@/state/session'
import type { Envelope, Outcome } from './types'

/** Which existing web service an operation belongs to: STERP.Api (online) or STERP.Jobs (batch). */
export type Target = 'api' | 'jobs'

/** RFC 9457 problem with the STERP extensions (contracts 4.6-4.7), or one the GUIv2 host raised itself. */
export interface Problem {
  type?: string
  title?: string
  status: number
  detail?: string | null
  reasonCode?: string | null
  returnCode?: number | null
  severity?: string | null
  correlationId?: string | null
  traceId?: string | null
  data?: unknown
  errors?: Record<string, string[]> | null
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: Problem

  constructor(problem: Problem) {
    super(problem.detail || problem.title || `HTTP ${problem.status}`)
    this.name = 'ApiError'
    this.status = problem.status
    this.problem = problem
  }

  get reasonCode(): string {
    return (this.problem.reasonCode ?? '').trim()
  }

  get title(): string {
    return this.problem.title ?? 'Request failed'
  }
}

export interface CallOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
  body?: unknown
  signal?: AbortSignal
  /** One stable key per user instruction where the legacy reused a correlation id (contracts 4.5). */
  idempotencyKey?: string
  /** Context to send instead of the current one (the context switcher validates a candidate first). */
  context?: RequestContext | null
}

/**
 * Calls an existing STERP operation through the GUIv2 host: '/api/bom/v1/...' on STERP.Api becomes '/bff/api/bom/v1/...',
 * '/api/jobs/v1/...' on STERP.Jobs becomes '/bff/jobs/jobs/v1/...'. Paths are written exactly as in the OpenAPI contracts.
 */
export async function call(target: Target, apiPath: string, options: CallOptions = {}): Promise<Response> {
  if (!apiPath.startsWith('/api/')) throw new Error(`Not an API path: ${apiPath}`)
  const url = `/bff/${target}/${apiPath.slice('/api/'.length)}`
  const headers = new Headers({ 'X-STERP-GUIv2': '1', Accept: 'application/json, text/plain;q=0.9, */*;q=0.1' })
  const context = options.context === undefined ? useSession.getState().context : options.context
  if (context) {
    if (context.companyCode) headers.set('X-STERP-Company', context.companyCode)
    if (context.companyCode && context.siteCode) headers.set('X-STERP-Site', context.siteCode)
    if (context.businessDate) headers.set('X-STERP-Business-Date', context.businessDate)
    if (context.runAs) headers.set('X-STERP-Run-As', context.runAs)
  }
  if (options.idempotencyKey) headers.set('Idempotency-Key', options.idempotencyKey)
  let body: BodyInit | undefined
  if (options.body !== undefined && options.method && options.method !== 'GET') {
    headers.set('Content-Type', 'application/json')
    body = JSON.stringify(options.body)
  }
  let response: Response
  try {
    response = await fetch(url, { method: options.method ?? 'GET', headers, body, signal: options.signal, cache: 'no-store', credentials: 'same-origin' })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError({ status: 0, title: 'GUIv2 host not reachable', detail: 'The GUIv2 host did not answer. Is run-sterp.bat running?', reasonCode: 'NETWORK' })
  }
  if (!response.ok) throw await toError(response)
  return response
}

async function toError(response: Response): Promise<ApiError> {
  const type = response.headers.get('Content-Type') ?? ''
  if (type.includes('json')) {
    try {
      const problem = (await response.json()) as Problem
      return new ApiError({ ...problem, status: problem.status ?? response.status })
    } catch {
      // fall through to the generic problem
    }
  }
  const text = type.startsWith('text/plain') ? (await response.text()).slice(0, 400) : ''
  return new ApiError({ status: response.status, title: response.statusText || `HTTP ${response.status}`, detail: text || null })
}

/** Direct (x-sterp-outcome: direct) operations: the body is the answer. */
export async function json<T>(target: Target, apiPath: string, options?: CallOptions): Promise<T> {
  const response = await call(target, apiPath, options)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/** Engine-backed (x-sterp-outcome: envelope) operations: outcome (RC 0, 4 or 6) plus data. */
export function envelope<T>(target: Target, apiPath: string, options?: CallOptions): Promise<Envelope<T>> {
  return json<Envelope<T>>(target, apiPath, options)
}

/** Procedure-backed list operations (contracts 4.10): the complete result set in legacy order. */
export async function list<T>(target: Target, apiPath: string, options?: CallOptions): Promise<T[]> {
  const body = await json<{ items: T[]; count: number }>(target, apiPath, options)
  return body?.items ?? []
}

/** SYSOUT and spool listings are stored bytes (text/plain; charset=windows-1252). */
export async function text(target: Target, apiPath: string, options?: CallOptions): Promise<string> {
  const response = await call(target, apiPath, options)
  const charset = /charset=([^;]+)/i.exec(response.headers.get('Content-Type') ?? '')?.[1]?.trim() ?? 'utf-8'
  const bytes = await response.arrayBuffer()
  try {
    return new TextDecoder(charset).decode(bytes)
  } catch {
    return new TextDecoder('windows-1252').decode(bytes)
  }
}

export function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === '') continue
    search.set(key, String(value))
  }
  const s = search.toString()
  return s ? `?${s}` : ''
}

export const seg = (value: string) => encodeURIComponent(value)

export function isWarning(outcome: Outcome | undefined | null): boolean {
  return !!outcome && outcome.returnCode >= 4
}

export function newInstructionKey(): string {
  return crypto.randomUUID()
}
