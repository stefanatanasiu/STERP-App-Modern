import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AnimatePresence, motion } from 'motion/react'
import { Building2, CalendarDays, Check, ChevronDown, Search, UserRound } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { platform } from '@/api/endpoints'
import { useSession } from '@/state/session'
import { switchContext } from '@/state/signon'
import { fmtDate, initials } from '@/domain/format'
import { Button, Kbd, cn } from '@/components/ui/primitives'
import { DateInput, Field, Select } from '@/components/ui/forms'
import { toast } from '@/components/ui/feedback'
import { useCommandPalette } from './CommandPalette'

export function TopBar() {
  const openPalette = useCommandPalette((s) => s.setOpen)
  return (
    <header className="relative z-30 flex h-16 shrink-0 items-center gap-4 border-b border-white/[0.06] bg-ink-950/30 px-6 backdrop-blur-xl">
      <button
        onClick={() => openPalette(true)}
        className="group flex h-10 w-full max-w-[440px] cursor-pointer items-center gap-3 rounded-xl bg-white/[0.04] px-3.5 text-left text-sm text-ink-400 ring-1 ring-white/[0.08] transition hover:bg-white/[0.06] hover:ring-white/15"
      >
        <Search className="size-4 text-ink-400 group-hover:text-ink-200" />
        <span className="flex-1 truncate">Search bills, items, pages…</span>
        <span className="flex items-center gap-1">
          <Kbd>Ctrl</Kbd>
          <Kbd>K</Kbd>
        </span>
      </button>
      <div className="flex-1" />
      <ContextSwitcher />
      <UserBadge />
    </header>
  )
}

function UserBadge() {
  const session = useSession((s) => s.session)
  if (!session) return null
  const name = session.user.displayName
  return (
    <div className="flex items-center gap-3 pl-1" title={`${session.windowsLogin} → ${session.title}`}>
      <div className="relative flex size-9 items-center justify-center rounded-full bg-[linear-gradient(135deg,#7c3aed,#0891b2)] text-[12px] font-bold text-white ring-2 ring-white/10">
        {initials(name)}
        <span className="absolute -right-0.5 -bottom-0.5 size-3 rounded-full bg-emerald-400 ring-2 ring-ink-950" />
      </div>
      <div className="hidden leading-tight lg:block">
        <div className="text-[13px] font-semibold text-ink-50">{session.user.userCode}</div>
        <div className="max-w-[160px] truncate text-[11px] text-ink-400">{session.isImpersonating ? `as ${session.title}` : name}</div>
      </div>
    </div>
  )
}

/** Company, site, business date and (for SYSADM) "continue as": validated by GET /api/platform/v1/session before adoption. */
function ContextSwitcher() {
  const session = useSession((s) => s.session)
  const context = useSession((s) => s.context)
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [pick, setPick] = useState('')
  const [date, setDate] = useState('')
  const [runAs, setRunAs] = useState('')
  const [busy, setBusy] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  const runAsUsers = useQuery({
    queryKey: ['run-as-users', session?.realUserCode],
    queryFn: ({ signal }) => platform.runAsUsers(signal),
    enabled: open && !!session && (session.isSysAdm || session.isImpersonating),
    staleTime: 10 * 60_000,
  })

  useEffect(() => {
    if (!open || !context) return
    setPick(`${context.companyCode}|${context.siteCode}`)
    setDate(context.businessDate)
    setRunAs(context.runAs ?? '')
  }, [open, context])

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => ref.current && !ref.current.contains(e.target as Node) && setOpen(false)
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    document.addEventListener('mousedown', onDown)
    window.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      window.removeEventListener('keydown', onKey)
    }
  }, [open])

  const companies = useMemo(() => {
    const map = new Map<string, { name: string; sites: { code: string; name: string }[] }>()
    for (const a of session?.access ?? []) {
      if (!map.has(a.companyCode)) map.set(a.companyCode, { name: a.companyName, sites: [] })
      map.get(a.companyCode)!.sites.push({ code: a.siteCode, name: a.siteName })
    }
    return [...map.entries()]
  }, [session])

  if (!session || !context) return null
  const selected = session.selected

  const apply = async () => {
    const [companyCode, siteCode] = pick.split('|')
    setBusy(true)
    try {
      const s = await switchContext({ companyCode, siteCode, businessDate: date || context.businessDate, runAs: runAs || null })
      await queryClient.invalidateQueries()
      toast.success('Context changed', `${s.selected?.companyName ?? companyCode} · ${s.selected?.siteName ?? siteCode} · ${fmtDate(s.businessDate)}${s.isImpersonating ? ` · as ${s.title}` : ''}`)
      setOpen(false)
    } catch (error) {
      toast.problem(error)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="relative" ref={ref}>
      <button
        onClick={() => setOpen((o) => !o)}
        className={cn(
          'flex h-10 cursor-pointer items-center gap-3 rounded-xl bg-white/[0.04] pr-2.5 pl-3 text-[13px] ring-1 ring-white/[0.08] transition hover:bg-white/[0.07]',
          open && 'bg-white/[0.07] ring-violet-300/30',
        )}
      >
        <Building2 className="size-4 text-violet-300" />
        <span className="font-semibold text-ink-50">{selected?.companyCode}</span>
        <span className="hidden max-w-[150px] truncate text-ink-300 md:inline">{selected?.siteName}</span>
        <span className="h-4 w-px bg-white/10" />
        <CalendarDays className="size-4 text-cyan-300" />
        <span className="text-ink-100 tabular">{fmtDate(session.businessDate)}</span>
        {session.fiscalPeriod && (
          <span className="hidden rounded-md bg-white/[0.06] px-1.5 py-0.5 text-[11px] text-ink-300 tabular lg:inline">{session.fiscalPeriod.periodText}</span>
        )}
        {session.isImpersonating && (
          <span className="rounded-md bg-pink-400/15 px-1.5 py-0.5 text-[11px] font-semibold text-pink-200">as {session.title.split(' ')[0]}</span>
        )}
        <ChevronDown className={cn('size-4 text-ink-400 transition-transform', open && 'rotate-180')} />
      </button>
      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, y: -6, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -6, scale: 0.98 }}
            transition={{ duration: 0.16 }}
            className="glass-strong absolute top-12 right-0 w-[380px] rounded-2xl p-4"
          >
            <div className="mb-2 text-[11px] font-semibold tracking-[0.1em] text-ink-400 uppercase">Company and site</div>
            <div className="flex max-h-[260px] flex-col gap-3 overflow-y-auto pr-1">
              {companies.map(([code, c]) => (
                <div key={code}>
                  <div className="mb-1.5 flex items-center gap-2 text-[12px] text-ink-300">
                    <span className="font-mono font-semibold text-ink-100">{code}</span>
                    {c.name}
                  </div>
                  <div className="grid grid-cols-2 gap-1.5">
                    {c.sites.map((s) => {
                      const key = `${code}|${s.code}`
                      const active = pick === key
                      return (
                        <button
                          key={key}
                          onClick={() => setPick(key)}
                          className={cn(
                            'flex cursor-pointer items-center gap-2 rounded-xl px-3 py-2 text-left ring-1 transition ring-inset',
                            active ? 'bg-violet-500/15 ring-violet-300/40' : 'bg-white/[0.03] ring-white/[0.07] hover:bg-white/[0.06]',
                          )}
                        >
                          <span className="min-w-0 flex-1">
                            <span className="block font-mono text-[12px] font-semibold text-ink-50">{s.code}</span>
                            <span className="block truncate text-[11px] text-ink-300">{s.name}</span>
                          </span>
                          {active && <Check className="size-4 text-violet-200" />}
                        </button>
                      )
                    })}
                  </div>
                </div>
              ))}
            </div>
            <div className="mt-4 grid grid-cols-1 gap-3">
              <Field label="Business date">
                <DateInput value={date} onChange={(e) => setDate(e.target.value)} />
              </Field>
              {(session.isSysAdm || session.isImpersonating) && (
                <Field label="Continue as" hint="SYSADM may work as another STERP user (X-STERP-Run-As).">
                  <Select value={runAs} onChange={(e) => setRunAs(e.target.value)}>
                    <option value="">{session.realUserCode} (yourself)</option>
                    {(runAsUsers.data ?? [])
                      .filter((u) => u.userCode !== session.realUserCode)
                      .map((u) => (
                        <option key={u.userCode} value={u.userCode}>
                          {u.userCode} · {u.displayName}
                        </option>
                      ))}
                  </Select>
                </Field>
              )}
            </div>
            <div className="mt-4 flex items-center justify-between gap-2">
              <span className="flex items-center gap-1.5 text-[11px] text-ink-400">
                <UserRound className="size-3.5" /> {session.windowsLogin}
              </span>
              <Button variant="primary" size="sm" loading={busy} onClick={apply}>
                Apply
              </Button>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  )
}
