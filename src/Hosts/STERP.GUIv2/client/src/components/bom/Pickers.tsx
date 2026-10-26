import { AnimatePresence, motion } from 'motion/react'
import { Box, Library, Search } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useCostSets, useLookup } from '@/api/hooks'
import { Select } from '@/components/ui/forms'
import { Spinner, StatusChip, cn } from '@/components/ui/primitives'
import { useDebounced } from '@/components/ui/useDebounced'

interface Option {
  key: string
  value: string
  label: ReactNode
  detail?: ReactNode
  meta?: ReactNode
  extra?: Record<string, unknown>
}

/** Combobox over the existing F2 lookup (platform_lookupMasterFile); free text is accepted as typed. */
function LookupPicker({
  maintCode,
  value,
  onChange,
  placeholder,
  icon,
  toOption,
  className,
  autoFocus,
}: {
  maintCode: string
  value: string
  onChange: (value: string, extra?: Record<string, unknown>) => void
  placeholder: string
  icon: ReactNode
  toOption: (row: Record<string, unknown>) => Option
  className?: string
  autoFocus?: boolean
}) {
  const [text, setText] = useState(value)
  const [open, setOpen] = useState(false)
  const [index, setIndex] = useState(0)
  const box = useRef<HTMLDivElement>(null)
  const search = useDebounced(text.trim(), 200)
  const lookup = useLookup(maintCode, search, open)
  const options = (lookup.data?.items ?? []).slice(0, 40).map(toOption)

  useEffect(() => {
    setText(value)
  }, [value])
  useEffect(() => {
    setIndex(0)
  }, [search])
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => box.current && !box.current.contains(e.target as Node) && commit(text)
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  })

  const commit = (v: string, extra?: Record<string, unknown>) => {
    setOpen(false)
    const clean = v.trim().toUpperCase()
    setText(clean)
    if (clean !== value || extra) onChange(clean, extra)
  }

  return (
    <div ref={box} className={cn('relative', className)}>
      <div className="relative">
        <span className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-ink-400">{icon}</span>
        <input
          value={text}
          autoFocus={autoFocus}
          placeholder={placeholder}
          onChange={(e) => {
            setText(e.target.value)
            setOpen(true)
          }}
          onFocus={() => setOpen(true)}
          // typing a code and moving on keeps it (option clicks keep the focus, so they still win)
          onBlur={() => {
            if (text.trim().toUpperCase() !== value) commit(text)
            else setOpen(false)
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault()
              setOpen(true)
              setIndex((i) => Math.min(options.length - 1, i + 1))
            } else if (e.key === 'ArrowUp') {
              e.preventDefault()
              setIndex((i) => Math.max(0, i - 1))
            } else if (e.key === 'Enter') {
              e.preventDefault()
              const o = open ? options[index] : undefined
              if (o && search.length > 0) commit(o.value, o.extra)
              else commit(text)
            } else if (e.key === 'Escape') {
              setOpen(false)
            }
          }}
          className="h-10 w-full rounded-xl bg-white/[0.045] pr-9 pl-9 font-mono text-[13px] text-ink-50 ring-1 ring-white/10 outline-none ring-inset placeholder:font-sans placeholder:text-ink-400 hover:ring-white/20 focus:bg-white/[0.07] focus:ring-2 focus:ring-violet-400/70"
          spellCheck={false}
        />
        <span className="absolute top-1/2 right-3 -translate-y-1/2">{lookup.isFetching ? <Spinner className="size-3.5" /> : <Search className="size-3.5 text-ink-500" />}</span>
      </div>
      <AnimatePresence>
        {open && options.length > 0 && (
          <motion.ul
            initial={{ opacity: 0, y: -4 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -4 }}
            transition={{ duration: 0.12 }}
            className="glass-strong absolute top-12 left-0 z-40 max-h-[340px] w-[min(520px,90vw)] overflow-y-auto rounded-2xl p-1.5"
          >
            {options.map((o, i) => (
              <li key={o.key}>
                <button
                  onMouseEnter={() => setIndex(i)}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => commit(o.value, o.extra)}
                  className={cn('flex w-full cursor-pointer items-center gap-3 rounded-xl px-3 py-2 text-left', i === index ? 'bg-white/[0.08]' : 'hover:bg-white/[0.04]')}
                >
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12.5px] text-ink-50">{o.label}</span>
                    {o.detail && <span className="block truncate text-[12px] text-ink-400">{o.detail}</span>}
                  </span>
                  {o.meta}
                </button>
              </li>
            ))}
          </motion.ul>
        )}
      </AnimatePresence>
    </div>
  )
}

export function ItemPicker(props: { value: string; onChange: (v: string) => void; placeholder?: string; className?: string; autoFocus?: boolean }) {
  return (
    <LookupPicker
      maintCode="ITM.ITEM"
      value={props.value}
      onChange={(v) => props.onChange(v)}
      placeholder={props.placeholder ?? 'Item code or description'}
      icon={<Box className="size-4" />}
      className={props.className}
      autoFocus={props.autoFocus}
      toOption={(r) => ({
        key: String(r.ItemCode),
        value: String(r.ItemCode),
        label: String(r.ItemCode),
        detail: String(r.Description ?? ''),
        meta: <span className="shrink-0 rounded-md bg-white/[0.06] px-1.5 py-0.5 text-[10px] font-semibold tracking-wide text-ink-300">{String(r.Category ?? r.ItemClass ?? '')}</span>,
      })}
    />
  )
}

export interface BillPick {
  bomRef: string
  revision: string
  parentItemCode: string
}

export function BillPicker(props: { value: string; onChange: (v: string, pick?: BillPick) => void; placeholder?: string; className?: string }) {
  return (
    <LookupPicker
      maintCode="BOM.HEADER"
      value={props.value}
      onChange={(v, extra) => props.onChange(v, extra as BillPick | undefined)}
      placeholder={props.placeholder ?? 'Bill reference or description'}
      icon={<Library className="size-4" />}
      className={props.className}
      toOption={(r) => ({
        key: `${r.BomRef}:${r.BomRevision}`,
        value: String(r.BomRef),
        label: (
          <>
            {String(r.BomRef)} <span className="text-ink-400">rev {String(r.BomRevision)}</span>
          </>
        ),
        detail: `${String(r.ParentItemCode)} · ${String(r.Description ?? '')}`,
        meta: <StatusChip status={String(r.Status ?? '')} />,
        extra: { bomRef: String(r.BomRef), revision: String(r.BomRevision), parentItemCode: String(r.ParentItemCode) },
      })}
    />
  )
}

export function CostSetSelect({ value, onChange, className }: { value: string; onChange: (v: string) => void; className?: string }) {
  const sets = useCostSets()
  const rows = sets.data ?? []
  return (
    <Select value={value} onChange={(e) => onChange(e.target.value)} className={className}>
      {!rows.some((r) => r.costSet === value) && <option value={value}>{value}</option>}
      {rows.map((r) => (
        <option key={r.costSet} value={r.costSet}>
          {r.costSet} · {r.status}
        </option>
      ))}
    </Select>
  )
}
