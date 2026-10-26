import { platform } from '@/api/endpoints'
import { ApiError } from '@/api/http'
import type { AccessEntry, SessionContext } from '@/api/types'
import { rememberedContext, useSession, type RequestContext } from '@/state/session'

/**
 * Sign-on as the classic GUI does it (ShellService.SignOnAtStartAsync): GET /api/platform/v1/session without context, then the
 * company/site to work in - the user's last choice when it is still in the access list, else the user's default, else the first
 * entry - confirmed by a second session call with that context.
 */
export async function signOn(): Promise<SessionContext> {
  const remembered = rememberedContext()
  let first: SessionContext
  try {
    first = await platform.session({ context: remembered.runAs ? { companyCode: '', siteCode: '', businessDate: '', runAs: remembered.runAs } : null })
  } catch (error) {
    // a remembered run-as user that is no longer allowed (403 API0003): sign on as oneself
    if (remembered.runAs && error instanceof ApiError && error.status === 403) first = await platform.session({ context: null })
    else throw error
  }
  const runAs = first.isImpersonating ? (remembered.runAs ?? null) : null
  const pick =
    find(first.access, remembered.companyCode, remembered.siteCode) ??
    find(first.access, first.user.defaultCompanyCode, first.user.defaultSiteCode) ??
    first.access[0] ??
    null
  if (!pick || (first.selected?.companyCode === pick.companyCode && first.selected?.siteCode === pick.siteCode)) {
    useSession.getState().accept(first, runAs)
    return first
  }
  return switchContext({ companyCode: pick.companyCode, siteCode: pick.siteCode, businessDate: first.businessDate, runAs })
}

/** Validates a new context with the API (the session operation checks the access list and the run-as rights) and adopts it. */
export async function switchContext(candidate: RequestContext): Promise<SessionContext> {
  const session = await platform.session({ context: candidate })
  useSession.getState().accept(session, candidate.runAs)
  return session
}

function find(access: AccessEntry[], company: string | null | undefined, site: string | null | undefined): AccessEntry | undefined {
  if (!company) return undefined
  return access.find((a) => a.companyCode === company && (!site || a.siteCode === site))
}
