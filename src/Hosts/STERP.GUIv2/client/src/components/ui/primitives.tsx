import { clsx, type ClassValue } from 'clsx'
import { Loader2 } from 'lucide-react'
import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react'
import { STATUS_TONE } from '@/domain/kinds'

export function cn(...values: ClassValue[]): string {
  return clsx(values)
}

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'warm'
type Size = 'xs' | 'sm' | 'md' | 'lg'

const VARIANTS: Record<Variant, string> = {
  primary:
    'text-white bg-[linear-gradient(135deg,#8b5cf6_0%,#6366f1_45%,#0891b2_100%)] shadow-[0_8px_30px_-8px_rgba(124,58,237,0.65)] hover:brightness-110 active:brightness-95 ring-1 ring-white/15',
  warm: 'text-ink-950 bg-[linear-gradient(135deg,#fde68a_0%,#f9a8d4_55%,#c4b5fd_100%)] shadow-[0_8px_30px_-10px_rgba(249,168,212,0.7)] hover:brightness-105 ring-1 ring-white/30',
  secondary: 'text-ink-100 bg-white/[0.06] hover:bg-white/[0.1] ring-1 ring-white/10 hover:ring-white/20',
  ghost: 'text-ink-200 hover:text-ink-50 hover:bg-white/[0.06]',
  danger: 'text-white bg-rose-500/85 hover:bg-rose-500 ring-1 ring-rose-300/30',
}

const SIZES: Record<Size, string> = {
  xs: 'h-7 px-2.5 text-xs gap-1.5 rounded-lg',
  sm: 'h-8 px-3 text-[13px] gap-1.5 rounded-lg',
  md: 'h-10 px-4 text-sm gap-2 rounded-xl',
  lg: 'h-12 px-5 text-[15px] gap-2.5 rounded-2xl',
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant
  size?: Size
  icon?: ReactNode
  loading?: boolean
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', icon, loading, className, children, disabled, ...rest },
  ref,
) {
  return (
    <button
      ref={ref}
      disabled={disabled || loading}
      className={cn(
        'inline-flex items-center justify-center font-medium whitespace-nowrap transition-all duration-200 select-none',
        'disabled:opacity-45 disabled:pointer-events-none cursor-pointer',
        VARIANTS[variant],
        SIZES[size],
        className,
      )}
      {...rest}
    >
      {loading ? <Loader2 className="size-4 animate-spin" /> : icon}
      {children}
    </button>
  )
})

export function IconButton({
  label,
  active,
  className,
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { label: string; active?: boolean }) {
  return (
    <button
      aria-label={label}
      title={label}
      className={cn(
        'inline-flex size-9 items-center justify-center rounded-xl text-ink-200 transition-all duration-200 cursor-pointer',
        'hover:bg-white/[0.08] hover:text-ink-50 disabled:opacity-40 disabled:pointer-events-none',
        active && 'bg-violet-500/20 text-violet-100 ring-1 ring-violet-300/30',
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  )
}

const TONES: Record<string, string> = {
  emerald: 'bg-emerald-400/10 text-emerald-300 ring-emerald-400/25',
  amber: 'bg-amber-400/10 text-amber-200 ring-amber-400/25',
  sky: 'bg-sky-400/10 text-sky-200 ring-sky-400/25',
  rose: 'bg-rose-400/10 text-rose-200 ring-rose-400/30',
  slate: 'bg-slate-400/10 text-slate-300 ring-slate-400/20',
  zinc: 'bg-zinc-400/10 text-zinc-300 ring-zinc-400/20',
  violet: 'bg-violet-400/12 text-violet-200 ring-violet-400/30',
  cyan: 'bg-cyan-400/10 text-cyan-200 ring-cyan-400/25',
  pink: 'bg-pink-400/10 text-pink-200 ring-pink-400/25',
}

export function Badge({ tone = 'slate', className, children, dot }: { tone?: string; className?: string; children: ReactNode; dot?: boolean }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[11px] font-semibold tracking-wide ring-1 ring-inset whitespace-nowrap',
        TONES[tone] ?? TONES.slate,
        className,
      )}
    >
      {dot && <span className="size-1.5 rounded-full bg-current opacity-80" />}
      {children}
    </span>
  )
}

/** A status code of an API answer (RELEASED, COMPLETE, FAILED ...) with its tone. */
export function StatusChip({ status, className }: { status: string | null | undefined; className?: string }) {
  if (!status) return null
  const s = status.trim()
  return (
    <Badge tone={STATUS_TONE[s] ?? 'slate'} dot className={className}>
      {s}
    </Badge>
  )
}

export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="inline-flex h-5 min-w-5 items-center justify-center rounded-md bg-white/[0.07] px-1.5 font-mono text-[10px] font-medium text-ink-300 ring-1 ring-white/10">
      {children}
    </kbd>
  )
}

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={cn('size-4 animate-spin text-violet-300', className)} />
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={cn('skeleton rounded-lg', className)} />
}

export function Code({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn('font-mono text-[12.5px] tracking-tight text-ink-100', className)}>{children}</span>
}
