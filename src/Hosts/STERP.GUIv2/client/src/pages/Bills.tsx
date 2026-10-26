import { motion } from 'motion/react'
import { ArrowUpRight, LayoutGrid, Library, List, Search } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router'
import { useLookup } from '@/api/hooks'
import { fmtDate } from '@/domain/format'
import { Badge, Skeleton, Spinner, StatusChip, cn } from '@/components/ui/primitives'
import { Segmented } from '@/components/ui/forms'
import { EmptyState } from '@/components/ui/surfaces'
import { ProblemCard } from '@/components/ui/feedback'
import { useDebounced } from '@/components/ui/useDebounced'
import { PageHeader } from '@/components/shell/PageHeader'

type Row = Record<string, unknown>
const s = (v: unknown) => (v === null || v === undefined ? '' : String(v))

export default function BillsPage() {
  const navigate = useNavigate()
  const [text, setText] = useState('')
  const [status, setStatus] = useState('ALL')
  const [layout, setLayout] = useState<'cards' | 'table'>('cards')
  const search = useDebounced(text.trim(), 250)
  const lookup = useLookup('BOM.HEADER', search)
  const rows = useMemo(() => lookup.data?.items ?? [], [lookup.data])
  const statuses = useMemo(() => {
    const counts = new Map<string, number>()
    for (const r of rows) counts.set(s(r.Status), (counts.get(s(r.Status)) ?? 0) + 1)
    return [...counts.entries()].sort((a, b) => b[1] - a[1])
  }, [rows])
  const shown = status === 'ALL' ? rows : rows.filter((r) => s(r.Status) === status)
  const open = (r: Row) => navigate(`/bills/${encodeURIComponent(s(r.BomRef))}?rev=${encodeURIComponent(s(r.BomRevision))}`)

  return (
    <div className="mx-auto flex max-w-[1500px] flex-col gap-6 p-8">
      <PageHeader
        eyebrow="Bills of material"
        title="Bill catalogue"
        description="Every bill revision of the company, as the BOM.HEADER master-file lookup returns them: released, draft and in review. Open one to see its structure, revisions and release state."
      />
      <div className="glass flex flex-wrap items-center gap-3 rounded-2xl p-3">
        <div className="relative min-w-[280px] flex-1">
          <Search className="absolute top-1/2 left-3.5 size-4 -translate-y-1/2 text-ink-400" />
          <input
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="Search bill reference, parent item or description…"
            className="h-11 w-full rounded-xl bg-white/[0.04] pr-10 pl-10 text-sm text-ink-50 ring-1 ring-white/10 outline-none ring-inset placeholder:text-ink-400 focus:ring-2 focus:ring-violet-400/60"
          />
          {lookup.isFetching && <Spinner className="absolute top-1/2 right-3.5 -translate-y-1/2" />}
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {[['ALL', rows.length] as [string, number], ...statuses].map(([st, n]) => (
            <button
              key={st}
              onClick={() => setStatus(st)}
              className={cn(
                'flex h-9 cursor-pointer items-center gap-2 rounded-xl px-3 text-[12.5px] font-medium ring-1 transition ring-inset',
                status === st ? 'bg-violet-500/20 text-white ring-violet-300/40' : 'bg-white/[0.03] text-ink-300 ring-white/[0.08] hover:text-ink-100',
              )}
            >
              {st === 'ALL' ? 'All' : st.charAt(0) + st.slice(1).toLowerCase()}
              <span className="rounded-full bg-white/[0.08] px-1.5 text-[10.5px] tabular">{n}</span>
            </button>
          ))}
        </div>
        <Segmented
          size="sm"
          value={layout}
          onChange={setLayout}
          options={[
            { value: 'cards', label: 'Cards', icon: <LayoutGrid className="size-3.5" /> },
            { value: 'table', label: 'Table', icon: <List className="size-3.5" /> },
          ]}
        />
      </div>

      {lookup.error && <ProblemCard error={lookup.error} />}
      {lookup.isPending && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {[...Array(8).keys()].map((i) => (
            <Skeleton key={i} className="h-44 rounded-2xl" />
          ))}
        </div>
      )}
      {!lookup.isPending && shown.length === 0 && !lookup.error && (
        <EmptyState icon={<Library className="size-6" />} title="No bills found">
          The lookup found nothing for “{search}”.
        </EmptyState>
      )}

      {layout === 'cards' ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {shown.map((r, i) => (
            <motion.button
              key={`${s(r.BomRef)}-${s(r.BomRevision)}`}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: Math.min(i, 20) * 0.02 }}
              onClick={() => open(r)}
              className="group glass relative flex cursor-pointer flex-col overflow-hidden rounded-2xl p-5 text-left transition hover:-translate-y-0.5 hover:ring-1 hover:ring-violet-300/30"
            >
              <span className="pointer-events-none absolute -top-16 -right-16 size-40 rounded-full bg-violet-500/10 opacity-0 blur-2xl transition group-hover:opacity-100" />
              <div className="flex items-start gap-2">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate font-mono text-[15px] font-semibold text-ink-50">{s(r.BomRef)}</span>
                    <span className="rounded-md bg-white/[0.07] px-1.5 py-0.5 font-mono text-[11px] text-ink-200">rev {s(r.BomRevision)}</span>
                  </div>
                  <div className="mt-1 font-mono text-[12px] text-violet-200/80">{s(r.ParentItemCode)}</div>
                </div>
                <StatusChip status={s(r.Status)} />
              </div>
              <p className="mt-3 line-clamp-2 min-h-[38px] text-[13px] leading-snug text-ink-300">{s(r.Description)}</p>
              <div className="mt-4 flex flex-wrap items-center gap-x-4 gap-y-1 text-[11.5px] text-ink-400">
                <span>from {fmtDate(s(r.EffectiveFrom))}</span>
                {r.EffectiveTo ? <span>to {fmtDate(s(r.EffectiveTo))}</span> : null}
                {r.IsPhantom === true && <Badge tone="slate">phantom</Badge>}
                {s(r.Purpose) && s(r.Purpose) !== 'STANDARD' && <Badge tone="sky">{s(r.Purpose)}</Badge>}
              </div>
              <div className="mt-3 flex items-center gap-2 border-t border-white/[0.06] pt-3 text-[11.5px] text-ink-400">
                {r.ReleasedBy ? (
                  <span>
                    released by <span className="text-ink-200">{s(r.ReleasedBy)}</span>
                  </span>
                ) : (
                  <span>author {s(r.AuthorUser) || '–'}</span>
                )}
                <ArrowUpRight className="ml-auto size-4 text-ink-500 transition group-hover:text-violet-200" />
              </div>
            </motion.button>
          ))}
        </div>
      ) : (
        <div className="glass overflow-hidden rounded-2xl">
          <table className="w-full text-[13px]">
            <thead className="bg-white/[0.03] text-left text-[10.5px] font-semibold tracking-[0.08em] text-ink-400 uppercase">
              <tr>
                {['Bill', 'Rev', 'Parent item', 'Description', 'Status', 'Effective from', 'Released by'].map((h) => (
                  <th key={h} className="px-4 py-3">
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {shown.map((r) => (
                <tr key={`${s(r.BomRef)}-${s(r.BomRevision)}`} onClick={() => open(r)} className="cursor-pointer border-t border-white/[0.05] hover:bg-white/[0.04]">
                  <td className="px-4 py-2.5 font-mono text-ink-50">{s(r.BomRef)}</td>
                  <td className="px-4 py-2.5 font-mono text-ink-200">{s(r.BomRevision)}</td>
                  <td className="px-4 py-2.5 font-mono text-violet-200/80">{s(r.ParentItemCode)}</td>
                  <td className="max-w-[420px] truncate px-4 py-2.5 text-ink-300">{s(r.Description)}</td>
                  <td className="px-4 py-2.5">
                    <StatusChip status={s(r.Status)} />
                  </td>
                  <td className="px-4 py-2.5 text-ink-300 tabular">{fmtDate(s(r.EffectiveFrom))}</td>
                  <td className="px-4 py-2.5 text-ink-300">{s(r.ReleasedBy) || '–'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {rows.length >= 200 && <p className="text-center text-[12px] text-ink-400">The lookup returns at most 200 rows; refine the search to narrow them down.</p>}
    </div>
  )
}
