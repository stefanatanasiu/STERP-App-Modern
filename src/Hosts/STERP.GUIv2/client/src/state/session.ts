import { create } from 'zustand'
import type { AccessEntry, SessionContext } from '@/api/types'

/** The request context the STERP contracts carry on every call (section 4.5): company, site, business date, SYSADM run-as. */
export interface RequestContext {
  companyCode: string
  siteCode: string
  businessDate: string
  runAs: string | null
}

interface SessionStore {
  session: SessionContext | null
  context: RequestContext | null
  /** The answer of GET /api/platform/v1/session becomes the context of every later call. */
  accept: (session: SessionContext, runAs: string | null) => void
  clear: () => void
}

const STORAGE_KEY = 'sterp.guiv2.context'

interface Remembered {
  companyCode?: string
  siteCode?: string
  runAs?: string | null
}

export function rememberedContext(): Remembered {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}') as Remembered
  } catch {
    return {}
  }
}

export const useSession = create<SessionStore>((set) => ({
  session: null,
  context: null,
  accept: (session, runAs) => {
    const selected: AccessEntry | null = session.selected
    const context: RequestContext = {
      companyCode: selected?.companyCode ?? '',
      siteCode: selected?.siteCode ?? '',
      businessDate: session.businessDate,
      runAs: session.isImpersonating ? runAs : null,
    }
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ companyCode: context.companyCode, siteCode: context.siteCode, runAs: context.runAs } satisfies Remembered),
    )
    set({ session, context })
  },
  clear: () => set({ session: null, context: null }),
}))

/** Part of every query key, so a context switch refetches everything that depends on it. */
export function contextKey(c: RequestContext | null): string {
  return c ? `${c.companyCode}|${c.siteCode}|${c.businessDate}|${c.runAs ?? ''}` : 'none'
}

export function useContextKey(): string {
  return useSession((s) => contextKey(s.context))
}
