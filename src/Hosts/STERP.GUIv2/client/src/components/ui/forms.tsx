import { motion } from 'motion/react'
import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react'
import { ChevronDown } from 'lucide-react'
import { cn } from './primitives'

const INPUT =
  'h-10 w-full rounded-xl bg-white/[0.045] px-3 text-sm text-ink-50 ring-1 ring-white/10 ring-inset outline-none transition ' +
  'placeholder:text-ink-400 hover:ring-white/20 focus:ring-2 focus:ring-violet-400/70 focus:bg-white/[0.07] disabled:opacity-50'

export function Field({ label, hint, children, className }: { label: string; hint?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <label className={cn('flex min-w-0 flex-col gap-1.5', className)}>
      <span className="text-[11px] font-semibold tracking-[0.08em] text-ink-300 uppercase">{label}</span>
      {children}
      {hint && <span className="text-[11px] text-ink-400">{hint}</span>}
    </label>
  )
}

export function TextInput({ className, ...rest }: InputHTMLAttributes<HTMLInputElement>) {
  return <input className={cn(INPUT, className)} {...rest} />
}

export function NumberInput({ className, ...rest }: InputHTMLAttributes<HTMLInputElement>) {
  return <input type="number" inputMode="decimal" className={cn(INPUT, 'tabular', className)} {...rest} />
}

export function DateInput({ className, ...rest }: InputHTMLAttributes<HTMLInputElement>) {
  return <input type="date" className={cn(INPUT, 'tabular', className)} {...rest} />
}

export function Select({ className, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <div className="relative">
      <select className={cn(INPUT, 'cursor-pointer appearance-none pr-9 [&>option]:bg-ink-850', className)} {...rest}>
        {children}
      </select>
      <ChevronDown className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-ink-300" />
    </div>
  )
}

export function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className="group inline-flex cursor-pointer items-center gap-2.5 text-sm text-ink-200 select-none"
    >
      <span
        className={cn(
          'relative h-5 w-9 rounded-full ring-1 ring-inset transition-colors duration-300',
          checked ? 'bg-violet-500/70 ring-violet-300/40' : 'bg-white/[0.08] ring-white/15',
        )}
      >
        <motion.span
          layout
          transition={{ type: 'spring', stiffness: 600, damping: 34 }}
          className={cn('absolute top-0.5 size-4 rounded-full bg-white shadow', checked ? 'left-[18px]' : 'left-0.5')}
        />
      </span>
      {label}
    </button>
  )
}

export interface SegmentOption<T extends string> {
  value: T
  label: ReactNode
  icon?: ReactNode
  title?: string
}

/** Pill selector with a sliding indicator. */
export function Segmented<T extends string>({
  options,
  value,
  onChange,
  size = 'md',
  className,
}: {
  options: SegmentOption<T>[]
  value: T
  onChange: (v: T) => void
  size?: 'sm' | 'md'
  className?: string
}) {
  const id = useId()
  return (
    <div className={cn('inline-flex rounded-xl bg-white/[0.04] p-1 ring-1 ring-white/10 ring-inset', className)} role="tablist">
      {options.map((o) => {
        const active = o.value === value
        return (
          <button
            key={o.value}
            role="tab"
            aria-selected={active}
            title={o.title}
            onClick={() => onChange(o.value)}
            className={cn(
              'relative inline-flex cursor-pointer items-center gap-1.5 rounded-lg font-medium whitespace-nowrap transition-colors',
              size === 'sm' ? 'h-7 px-2.5 text-xs' : 'h-8 px-3 text-[13px]',
              active ? 'text-white' : 'text-ink-300 hover:text-ink-100',
            )}
          >
            {active && (
              <motion.span
                layoutId={`seg-${id}`}
                transition={{ type: 'spring', stiffness: 500, damping: 38 }}
                className="absolute inset-0 rounded-lg bg-[linear-gradient(135deg,rgba(139,92,246,0.55),rgba(14,165,233,0.4))] shadow-[0_4px_18px_-6px_rgba(139,92,246,0.8)] ring-1 ring-white/15"
              />
            )}
            <span className="relative z-10 inline-flex items-center gap-1.5">
              {o.icon}
              {o.label}
            </span>
          </button>
        )
      })}
    </div>
  )
}
