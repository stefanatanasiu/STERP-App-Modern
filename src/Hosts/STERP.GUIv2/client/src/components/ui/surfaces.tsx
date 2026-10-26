import { AnimatePresence, motion } from 'motion/react'
import { useEffect, useId, type ReactNode } from 'react'
import { X } from 'lucide-react'
import { cn } from './primitives'

export function Panel({
  title,
  subtitle,
  actions,
  icon,
  children,
  className,
  bodyClassName,
}: {
  title?: ReactNode
  subtitle?: ReactNode
  actions?: ReactNode
  icon?: ReactNode
  children?: ReactNode
  className?: string
  bodyClassName?: string
}) {
  return (
    <section className={cn('glass flex min-w-0 flex-col rounded-2xl', className)}>
      {(title || actions) && (
        <header className="flex items-center gap-3 px-5 pt-4 pb-3">
          {icon && <span className="flex size-8 items-center justify-center rounded-xl bg-white/[0.06] text-violet-200 ring-1 ring-white/10">{icon}</span>}
          <div className="min-w-0 flex-1">
            {title && <h3 className="truncate text-[15px] font-semibold tracking-tight text-ink-50">{title}</h3>}
            {subtitle && <p className="truncate text-xs text-ink-300">{subtitle}</p>}
          </div>
          {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={cn('min-h-0 flex-1 px-5 pb-5', !title && !actions && 'pt-5', bodyClassName)}>{children}</div>
    </section>
  )
}

export function Stat({
  label,
  value,
  hint,
  accent,
  title,
  className,
}: {
  label: ReactNode
  value: ReactNode
  hint?: ReactNode
  accent?: string
  title?: string
  className?: string
}) {
  return (
    <div className={cn('relative overflow-hidden rounded-2xl bg-white/[0.035] px-4 py-3.5 ring-1 ring-white/[0.08] ring-inset', className)} title={title}>
      {accent && <span className="absolute inset-x-0 top-0 h-px" style={{ background: `linear-gradient(90deg, transparent, ${accent}, transparent)` }} />}
      <div className="text-[11px] font-semibold tracking-[0.08em] text-ink-300 uppercase">{label}</div>
      <div className="mt-1 truncate text-xl font-semibold tracking-tight text-ink-50 tabular">{value}</div>
      {hint && <div className="mt-0.5 truncate text-xs text-ink-400">{hint}</div>}
    </div>
  )
}

export function KeyValues({ items, columns = 2 }: { items: [ReactNode, ReactNode][]; columns?: 1 | 2 | 3 }) {
  return (
    <dl className={cn('grid gap-x-6 gap-y-3', columns === 1 && 'grid-cols-1', columns === 2 && 'grid-cols-2', columns === 3 && 'grid-cols-3')}>
      {items.map(([k, v], i) => (
        <div key={i} className="min-w-0">
          <dt className="text-[11px] font-medium tracking-[0.06em] text-ink-400 uppercase">{k}</dt>
          <dd className="mt-0.5 truncate text-sm text-ink-100 tabular">{v ?? '–'}</dd>
        </div>
      ))}
    </dl>
  )
}

export function EmptyState({ icon, title, children, className }: { icon?: ReactNode; title: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-3 px-6 py-14 text-center', className)}>
      {icon && (
        <div className="relative flex size-14 items-center justify-center rounded-2xl bg-white/[0.05] text-violet-200 ring-1 ring-white/10">
          <span className="absolute inset-0 rounded-2xl bg-violet-500/10 blur-xl" />
          <span className="relative">{icon}</span>
        </div>
      )}
      <h3 className="text-base font-semibold text-ink-50">{title}</h3>
      {children && <div className="max-w-md text-sm leading-relaxed text-ink-300">{children}</div>}
    </div>
  )
}

export interface TabDef<T extends string> {
  value: T
  label: ReactNode
  count?: number
}

export function Tabs<T extends string>({ tabs, value, onChange, className }: { tabs: TabDef<T>[]; value: T; onChange: (v: T) => void; className?: string }) {
  const id = useId()
  return (
    <div className={cn('flex items-center gap-1 border-b border-white/[0.07]', className)} role="tablist">
      {tabs.map((t) => {
        const active = t.value === value
        return (
          <button
            key={t.value}
            role="tab"
            aria-selected={active}
            onClick={() => onChange(t.value)}
            className={cn(
              'relative -mb-px inline-flex cursor-pointer items-center gap-2 px-3.5 py-2.5 text-[13px] font-medium transition-colors',
              active ? 'text-ink-50' : 'text-ink-300 hover:text-ink-100',
            )}
          >
            {t.label}
            {t.count !== undefined && (
              <span className={cn('rounded-full px-1.5 text-[10px] tabular', active ? 'bg-violet-400/20 text-violet-100' : 'bg-white/[0.06] text-ink-300')}>
                {t.count}
              </span>
            )}
            {active && (
              <motion.span
                layoutId={`tab-${id}`}
                className="absolute inset-x-2 -bottom-px h-0.5 rounded-full bg-[linear-gradient(90deg,#a78bfa,#38bdf8)]"
                transition={{ type: 'spring', stiffness: 500, damping: 40 }}
              />
            )}
          </button>
        )
      })}
    </div>
  )
}

export function Dialog({
  open,
  onClose,
  title,
  subtitle,
  children,
  footer,
  width = 'max-w-lg',
}: {
  open: boolean
  onClose: () => void
  title: ReactNode
  subtitle?: ReactNode
  children: ReactNode
  footer?: ReactNode
  width?: string
}) {
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])
  return (
    <AnimatePresence>
      {open && (
        <motion.div className="fixed inset-0 z-50 flex items-center justify-center p-4" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}>
          <div className="absolute inset-0 bg-ink-950/70 backdrop-blur-sm" onClick={onClose} />
          <motion.div
            role="dialog"
            aria-modal="true"
            initial={{ opacity: 0, y: 16, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 10, scale: 0.98 }}
            transition={{ type: 'spring', stiffness: 380, damping: 30 }}
            className={cn('glass-strong relative w-full rounded-3xl', width)}
          >
            <header className="flex items-start gap-3 px-6 pt-5">
              <div className="min-w-0 flex-1">
                <h2 className="text-lg font-semibold tracking-tight text-ink-50">{title}</h2>
                {subtitle && <p className="mt-0.5 text-sm text-ink-300">{subtitle}</p>}
              </div>
              <button onClick={onClose} aria-label="Close" className="cursor-pointer rounded-lg p-1.5 text-ink-300 hover:bg-white/[0.08] hover:text-ink-50">
                <X className="size-4" />
              </button>
            </header>
            <div className="px-6 py-5">{children}</div>
            {footer && <footer className="flex justify-end gap-2 border-t border-white/[0.07] px-6 py-4">{footer}</footer>}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  )
}
