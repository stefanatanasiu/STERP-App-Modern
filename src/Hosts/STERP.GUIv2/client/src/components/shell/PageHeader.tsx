import type { ReactNode } from 'react'
import { cn } from '@/components/ui/primitives'

export function PageHeader({
  eyebrow,
  title,
  description,
  actions,
  className,
}: {
  eyebrow?: ReactNode
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  className?: string
}) {
  return (
    <div className={cn('flex flex-wrap items-end gap-4', className)}>
      <div className="min-w-0 flex-1">
        {eyebrow && <div className="mb-1.5 text-[11px] font-semibold tracking-[0.16em] text-violet-300/90 uppercase">{eyebrow}</div>}
        <h1 className="text-[28px] leading-tight font-semibold tracking-[-0.02em] text-ink-50">{title}</h1>
        {description && <p className="mt-1.5 max-w-3xl text-[14px] leading-relaxed text-ink-300">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}
