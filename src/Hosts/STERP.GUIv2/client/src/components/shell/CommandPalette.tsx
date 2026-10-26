import { AnimatePresence, motion } from 'motion/react'
import { ArrowRight, Box, CornerDownLeft, Library, Search } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { create } from 'zustand'
import { useLookup } from '@/api/hooks'
import { Kbd, Spinner, cn } from '@/components/ui/primitives'
import { useDebounced } from '@/components/ui/useDebounced'
import { NAV } from './Sidebar'

interface PaletteStore {
  open: boolean
  setOpen: (open: boolean) => void
}

export const useCommandPalette = create<PaletteStore>((set) => ({ open: false, setOpen: (open) => set({ open }) }))

interface Entry {
  key: string
  group: string
  icon: ReactNode
  title: ReactNode
  subtitle?: ReactNode
  meta?: ReactNode
  go: string
}

/** Ctrl+K: pages, bills (BOM.HEADER lookup) and items (ITM.ITEM lookup), all answered by the existing lookup operation. */
export function CommandPalette() {
  const { open, setOpen } = useCommandPalette()
  const [text, setText] = useState('')
  const [index, setIndex] = useState(0)
  const navigate = useNavigate()
  const input = useRef<HTMLInputElement>(null)
  const search = useDebounced(text.trim(), 180)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setOpen(!useCommandPalette.getState().open)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [setOpen])

  useEffect(() => {
    if (open) {
      setText('')
      setIndex(0)
      window.setTimeout(() => input.current?.focus(), 30)
    }
  }, [open])

  const bills = useLookup('BOM.HEADER', search, open && search.length > 0)
  const items = useLookup('ITM.ITEM', search, open && search.length > 0)

  const entries = useMemo<Entry[]>(() => {
    const q = search.toLowerCase()
    const pages: Entry[] = NAV.flatMap((g) => g.items)
      .filter((p) => !q || p.label.toLowerCase().includes(q))
      .map((p) => ({ key: `page:${p.to}`, group: 'Go to', icon: p.icon, title: p.label, go: p.to }))
    const billRows: Entry[] = search
      ? (bills.data?.items ?? []).slice(0, 7).map((r) => ({
          key: `bill:${r.BomRef}:${r.BomRevision}`,
          group: 'Bills',
          icon: <Library className="size-[18px]" />,
          title: (
            <span className="font-mono">
              {String(r.BomRef)} <span className="text-ink-400">rev {String(r.BomRevision)}</span>
            </span>
          ),
          subtitle: String(r.Description ?? ''),
          meta: String(r.Status ?? ''),
          go: `/bills/${encodeURIComponent(String(r.BomRef))}?rev=${encodeURIComponent(String(r.BomRevision))}`,
        }))
      : []
    const itemRows: Entry[] = search
      ? (items.data?.items ?? []).slice(0, 7).map((r) => ({
          key: `item:${r.ItemCode}`,
          group: 'Items',
          icon: <Box className="size-[18px]" />,
          title: <span className="font-mono">{String(r.ItemCode)}</span>,
          subtitle: String(r.Description ?? ''),
          meta: String(r.Category ?? r.ItemClass ?? ''),
          go: `/explode/${encodeURIComponent(String(r.ItemCode))}`,
        }))
      : []
    return [...billRows, ...itemRows, ...pages]
  }, [search, bills.data, items.data])

  useEffect(() => {
    setIndex(0)
  }, [search])

  const choose = (e: Entry | undefined) => {
    if (!e) return
    setOpen(false)
    navigate(e.go)
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setIndex((i) => Math.min(entries.length - 1, i + 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setIndex((i) => Math.max(0, i - 1))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      choose(entries[index])
    } else if (e.key === 'Escape') {
      setOpen(false)
    }
  }

  let lastGroup = ''
  const loading = search.length > 0 && (bills.isFetching || items.isFetching)

  return (
    <AnimatePresence>
      {open && (
        <motion.div className="fixed inset-0 z-50 flex items-start justify-center px-4 pt-[12vh]" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}>
          <div className="absolute inset-0 bg-ink-950/70 backdrop-blur-sm" onClick={() => setOpen(false)} />
          <motion.div
            initial={{ opacity: 0, y: -12, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -8, scale: 0.98 }}
            transition={{ type: 'spring', stiffness: 420, damping: 34 }}
            className="glass-strong relative w-full max-w-[640px] overflow-hidden rounded-3xl"
            onKeyDown={onKeyDown}
          >
            <div className="flex items-center gap-3 border-b border-white/[0.07] px-5">
              <Search className="size-5 text-violet-300" />
              <input
                ref={input}
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="Search bills and items, or jump to a page…"
                className="h-14 flex-1 bg-transparent text-[15px] text-ink-50 outline-none placeholder:text-ink-400"
              />
              {loading && <Spinner />}
              <Kbd>Esc</Kbd>
            </div>
            <div className="max-h-[420px] overflow-y-auto p-2">
              {entries.length === 0 && <div className="px-4 py-10 text-center text-sm text-ink-400">Nothing found for “{search}”.</div>}
              {entries.map((e, i) => {
                const header = e.group !== lastGroup ? e.group : null
                lastGroup = e.group
                return (
                  <div key={e.key}>
                    {header && <div className="px-3 pt-3 pb-1.5 text-[10.5px] font-semibold tracking-[0.14em] text-ink-400 uppercase">{header}</div>}
                    <button
                      onMouseEnter={() => setIndex(i)}
                      onClick={() => choose(e)}
                      className={cn(
                        'flex w-full cursor-pointer items-center gap-3 rounded-xl px-3 py-2.5 text-left transition-colors',
                        i === index ? 'bg-white/[0.08]' : 'hover:bg-white/[0.04]',
                      )}
                    >
                      <span className={cn('flex size-9 shrink-0 items-center justify-center rounded-xl ring-1 ring-white/10', i === index ? 'bg-violet-500/20 text-violet-100' : 'bg-white/[0.04] text-ink-300')}>
                        {e.icon}
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-[13.5px] text-ink-50">{e.title}</span>
                        {e.subtitle && <span className="block truncate text-[12px] text-ink-400">{e.subtitle}</span>}
                      </span>
                      {e.meta && <span className="shrink-0 text-[11px] text-ink-400">{e.meta}</span>}
                      {i === index ? <CornerDownLeft className="size-4 text-ink-300" /> : <ArrowRight className="size-4 text-transparent" />}
                    </button>
                  </div>
                )
              })}
            </div>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  )
}
