import { AnimatePresence, motion } from 'motion/react'
import { AlertTriangle, CheckCircle2, Info, OctagonAlert, X } from 'lucide-react'
import type { ReactNode } from 'react'
import { create } from 'zustand'
import { ApiError } from '@/api/http'
import type { Outcome } from '@/api/types'
import { Code, cn } from './primitives'

// ---------------------------------------------------------------- toasts

type ToastTone = 'success' | 'error' | 'info' | 'warning'

interface ToastItem {
  id: number
  tone: ToastTone
  title: string
  description?: string
}

interface ToastStore {
  items: ToastItem[]
  push: (t: Omit<ToastItem, 'id'>) => void
  dismiss: (id: number) => void
}

let nextToast = 1

const useToasts = create<ToastStore>((set) => ({
  items: [],
  push: (t) => {
    const id = nextToast++
    set((s) => ({ items: [...s.items.slice(-3), { ...t, id }] }))
    window.setTimeout(() => set((s) => ({ items: s.items.filter((i) => i.id !== id) })), t.tone === 'error' ? 9000 : 5000)
  },
  dismiss: (id) => set((s) => ({ items: s.items.filter((i) => i.id !== id) })),
}))

export const toast = {
  success: (title: string, description?: string) => useToasts.getState().push({ tone: 'success', title, description }),
  info: (title: string, description?: string) => useToasts.getState().push({ tone: 'info', title, description }),
  warning: (title: string, description?: string) => useToasts.getState().push({ tone: 'warning', title, description }),
  error: (title: string, description?: string) => useToasts.getState().push({ tone: 'error', title, description }),
  problem: (error: unknown) => {
    const e = asApiError(error)
    useToasts.getState().push({ tone: 'error', title: e.reasonCode ? `${e.title} · ${e.reasonCode}` : e.title, description: e.message })
  },
}

const TOAST_ICON: Record<ToastTone, ReactNode> = {
  success: <CheckCircle2 className="size-5 text-emerald-300" />,
  error: <OctagonAlert className="size-5 text-rose-300" />,
  info: <Info className="size-5 text-sky-300" />,
  warning: <AlertTriangle className="size-5 text-amber-300" />,
}

export function Toaster() {
  const { items, dismiss } = useToasts()
  return (
    <div className="pointer-events-none fixed right-5 bottom-5 z-[60] flex w-[380px] flex-col gap-2.5">
      <AnimatePresence initial={false}>
        {items.map((t) => (
          <motion.div
            key={t.id}
            layout
            initial={{ opacity: 0, x: 40, scale: 0.96 }}
            animate={{ opacity: 1, x: 0, scale: 1 }}
            exit={{ opacity: 0, x: 40, scale: 0.96 }}
            transition={{ type: 'spring', stiffness: 420, damping: 32 }}
            className="glass-strong pointer-events-auto flex items-start gap-3 rounded-2xl px-4 py-3.5"
          >
            <span className="mt-0.5">{TOAST_ICON[t.tone]}</span>
            <div className="min-w-0 flex-1">
              <p className="text-sm font-semibold text-ink-50">{t.title}</p>
              {t.description && <p className="mt-0.5 text-[13px] leading-snug text-ink-300">{t.description}</p>}
            </div>
            <button onClick={() => dismiss(t.id)} className="cursor-pointer rounded-md p-1 text-ink-400 hover:text-ink-100" aria-label="Dismiss">
              <X className="size-3.5" />
            </button>
          </motion.div>
        ))}
      </AnimatePresence>
    </div>
  )
}

// ---------------------------------------------------------------- API answers

export function asApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error
  const message = error instanceof Error ? error.message : String(error)
  return new ApiError({ status: 0, title: 'Unexpected error', detail: message })
}

/** A refusal or failure of an existing STERP operation, shown with its legacy reason code and correlation id. */
export function ProblemCard({ error, className, action }: { error: unknown; className?: string; action?: ReactNode }) {
  const e = asApiError(error)
  const p = e.problem
  const severe = e.status >= 500 || e.status === 0
  return (
    <div
      className={cn(
        'flex items-start gap-3.5 rounded-2xl px-4 py-3.5 ring-1 ring-inset',
        severe ? 'bg-rose-500/[0.08] ring-rose-400/25' : 'bg-amber-400/[0.07] ring-amber-300/25',
        className,
      )}
    >
      <span className={cn('mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-xl', severe ? 'bg-rose-400/15 text-rose-200' : 'bg-amber-300/15 text-amber-200')}>
        {severe ? <OctagonAlert className="size-4.5" /> : <AlertTriangle className="size-4.5" />}
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <p className="text-sm font-semibold text-ink-50">{e.title}</p>
          {e.reasonCode && <Code className="rounded-md bg-white/[0.07] px-1.5 py-0.5 text-[11px] text-ink-200">{e.reasonCode}</Code>}
          {e.status > 0 && <span className="text-[11px] text-ink-400 tabular">HTTP {e.status}</span>}
        </div>
        <p className="mt-1 text-[13px] leading-relaxed text-ink-200">{e.message}</p>
        {(p.correlationId || p.traceId) && (
          <p className="mt-1.5 truncate font-mono text-[10.5px] text-ink-400">
            {p.correlationId && <>correlation {p.correlationId}</>}
            {p.correlationId && p.traceId && ' · '}
            {p.traceId && <>trace {p.traceId}</>}
          </p>
        )}
      </div>
      {action}
    </div>
  )
}

/** RC 4 (WARNING) and RC 6 (HOLD) outcomes: the answer is valid, the legacy message still matters. */
export function OutcomeBanner({ outcome, className }: { outcome: Outcome | null | undefined; className?: string }) {
  if (!outcome || outcome.returnCode === 0) return null
  return (
    <div className={cn('flex items-start gap-3 rounded-2xl bg-amber-400/[0.07] px-4 py-3 ring-1 ring-amber-300/25 ring-inset', className)}>
      <AlertTriangle className="mt-0.5 size-4.5 shrink-0 text-amber-300" />
      <div className="min-w-0 text-[13px] leading-relaxed">
        <span className="font-semibold text-amber-100">
          {outcome.severity} · RC {outcome.returnCode}
          {outcome.reasonCode.trim() && <Code className="ml-2 text-[11px] text-amber-100/80">{outcome.reasonCode.trim()}</Code>}
        </span>
        {outcome.message && <span className="ml-2 text-ink-200">{outcome.message}</span>}
      </div>
    </div>
  )
}
