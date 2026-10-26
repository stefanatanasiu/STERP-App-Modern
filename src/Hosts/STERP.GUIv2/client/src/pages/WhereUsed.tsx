import { ArrowUpRight, GitFork, Layers3, Network, Orbit, Search } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { useWhereUsed } from '@/api/hooks'
import type { WhereUsedRow } from '@/api/types'
import { buildWhereUsedTree, type UseNode } from '@/domain/tree'
import { exact, fmtDate, fmtQty } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, Code, Skeleton, StatusChip, cn } from '@/components/ui/primitives'
import { DateInput, Field, NumberInput, Segmented, Toggle } from '@/components/ui/forms'
import { EmptyState, Stat } from '@/components/ui/surfaces'
import { ProblemCard } from '@/components/ui/feedback'
import { ItemPicker } from '@/components/bom/Pickers'
import { Tree3D } from '@/three/tree/Tree3D'
import type { LayoutKind } from '@/three/tree/layout'
import type { VizNode } from '@/three/tree/viz'

export default function WhereUsedPage() {
  const { item: itemParam } = useParams()
  const [search, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const session = useSession((s) => s.session)!
  const item = (itemParam ?? '').trim().toUpperCase()
  const asOf = search.get('asOf') ?? session.businessDate
  const levels = Number(search.get('levels') ?? '25') || 25
  const draft = (search.get('draft') ?? 'true') === 'true'
  const query = useWhereUsed(item || undefined, asOf, levels, draft)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [layout, setLayout] = useState<LayoutKind>('tiers')
  const [draftAsOf, setDraftAsOf] = useState(asOf)
  const [draftLevels, setDraftLevels] = useState(levels)

  const built = useMemo(() => (query.data ? buildWhereUsedTree(item, query.data) : null), [query.data, item])
  const viz = useMemo(() => (built ? toViz(built.root) : null), [built])
  const selected = (selectedId && built?.nodes.find((n) => n.id === selectedId)) || null
  const rows = query.data ?? []
  const tops = built ? built.nodes.filter((n) => n.depth > 0 && n.children.length === 0) : []

  const set = (patch: Record<string, string>) => {
    const next = new URLSearchParams(search)
    for (const [k, v] of Object.entries(patch)) next.set(k, v)
    setSearch(next, { replace: true })
  }

  return (
    <div className="flex h-full min-h-[680px] flex-col gap-4 p-5">
      <div className="glass relative z-20 flex flex-wrap items-end gap-3 rounded-3xl px-5 py-4">
        <div className="mr-2 flex items-center gap-3 self-center">
          <div className="flex size-10 items-center justify-center rounded-2xl bg-[linear-gradient(135deg,rgba(34,211,238,0.3),rgba(139,92,246,0.3))] ring-1 ring-white/15">
            <GitFork className="size-5 rotate-180 text-cyan-100" />
          </div>
          <div className="hidden min-[1700px]:block">
            <div className="text-[15px] font-semibold text-ink-50">Where used</div>
            <div className="text-[11.5px] text-ink-400">bom_getWhereUsed</div>
          </div>
        </div>
        <Field label="Component" className="w-[280px]">
          <ItemPicker value={item} onChange={(v) => v && navigate(`/where-used/${encodeURIComponent(v)}?${search.toString()}`)} />
        </Field>
        <Field label="As at" className="w-[150px]">
          <DateInput value={draftAsOf} onChange={(e) => setDraftAsOf(e.target.value)} />
        </Field>
        <Field label="Levels" className="w-[90px]">
          <NumberInput min={1} max={50} value={draftLevels} onChange={(e) => setDraftLevels(Number(e.target.value))} />
        </Field>
        <div className="flex h-10 items-center">
          <Toggle checked={draft} onChange={(v) => set({ draft: String(v) })} label="Include drafts" />
        </div>
        <Button variant="primary" icon={<Search className="size-4" />} loading={query.isFetching} onClick={() => set({ asOf: draftAsOf, levels: String(draftLevels || 25) })}>
          Trace upwards
        </Button>
      </div>

      {!item ? (
        <div className="glass flex flex-1 items-center justify-center rounded-3xl">
          <EmptyState icon={<GitFork className="size-6 rotate-180" />} title="Trace a component up to every product that uses it">
            Pick a component: the where-used enquiry climbs the bills level by level and GUIv2 grows the answer upwards in 3D. Try <Code>DEMO-PIN</Code> or{' '}
            <Code>ING-SUGAR-ICING</Code>.
          </EmptyState>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <Stat label="Uses" value={query.isPending ? '…' : rows.length} hint="rows of the enquiry" accent="#22d3ee" />
            <Stat label="Direct parents" value={query.isPending ? '…' : rows.filter((r) => r.isDirect).length} hint="level 1" accent="#a78bfa" />
            <Stat label="Levels reached" value={query.isPending ? '…' : rows.reduce((m, r) => Math.max(m, r.level), 0)} hint={`of at most ${levels}`} accent="#f9a8d4" />
            <Stat label="Top of the chains" value={query.isPending ? '…' : tops.length} hint="parents nothing above uses" accent="#fbbf24" />
          </div>
          <div className="flex min-h-0 flex-1 gap-4">
            <div className="glass relative min-w-0 flex-1 overflow-hidden rounded-3xl">
              {query.error ? (
                <div className="flex h-full items-center justify-center p-8">
                  <ProblemCard error={query.error} className="max-w-xl" />
                </div>
              ) : !viz ? (
                <div className="flex h-full items-center justify-center text-sm text-ink-400">Tracing…</div>
              ) : rows.length === 0 ? (
                <EmptyState icon={<GitFork className="size-6 rotate-180" />} title={`${item} is not used in any bill`} className="h-full">
                  The enquiry found no bill using it as at {fmtDate(asOf)}.
                </EmptyState>
              ) : (
                <>
                  <Tree3D
                    root={viz}
                    layout={layout}
                    collapsed={EMPTY}
                    selectedId={selectedId}
                    onSelect={setSelectedId}
                    onToggle={() => undefined}
                    labels="all"
                    autoRotate={!selectedId && layout !== 'tiers'}
                    upwards
                  />
                  <Segmented<LayoutKind>
                    size="sm"
                    className="glass-strong absolute top-4 left-4 z-10"
                    value={layout}
                    onChange={setLayout}
                    options={[
                      { value: 'tiers', label: 'Tiers', icon: <Layers3 className="size-3.5" /> },
                      { value: 'cone', label: 'Cone', icon: <Network className="size-3.5" /> },
                      { value: 'orbit', label: 'Orbit', icon: <Orbit className="size-3.5" /> },
                    ]}
                  />
                  <div className="glass-strong absolute bottom-4 left-4 z-10 flex items-center gap-4 rounded-2xl px-4 py-2.5 text-[11.5px] text-ink-200">
                    <span className="flex items-center gap-1.5">
                      <span className="size-2 rounded-full bg-cyan-400 shadow-[0_0_8px_#22d3ee]" /> component
                    </span>
                    <span className="flex items-center gap-1.5">
                      <span className="size-2 rounded-full bg-violet-400 shadow-[0_0_8px_#a78bfa]" /> uses it
                    </span>
                    <span className="flex items-center gap-1.5">
                      <span className="size-2 rounded-full bg-pink-300 shadow-[0_0_8px_#f9a8d4]" /> top of a chain
                    </span>
                  </div>
                </>
              )}
            </div>
            <aside className="glass w-[400px] shrink-0 overflow-y-auto rounded-3xl p-5">
              {selected && selected.depth > 0 ? (
                <UseDetail node={selected} onExplode={(code) => navigate(`/explode/${encodeURIComponent(code)}`)} onBill={(r) => navigate(`/bills/${encodeURIComponent(r.bomRef)}?rev=${encodeURIComponent(r.bomRevision)}`)} />
              ) : (
                <RowsList rows={rows} loading={query.isPending} onPick={(r) => setSelectedId(built?.nodes.find((n) => n.rows.includes(r))?.id ?? null)} />
              )}
            </aside>
          </div>
        </>
      )}
    </div>
  )
}

const EMPTY = new Set<string>()

function toViz(root: UseNode): VizNode {
  const map = (n: UseNode, parent: VizNode | null): VizNode => {
    const row = n.rows[0]
    const v: VizNode = {
      id: n.id,
      depth: n.depth,
      parent,
      children: [],
      cls: n.depth === 0 ? 'bought' : n.children.length === 0 ? 'root' : 'made',
      code: n.item,
      title: row ? `${row.bomRef} rev ${row.bomRevision} · ${row.status}` : 'the component',
      figure: row ? `${fmtQty(row.usageQty, true)} ${row.uomCode}` : undefined,
      flagged: n.rows.some((r) => r.status !== 'RELEASED'),
    }
    v.children = n.children.map((c) => map(c, v))
    return v
  }
  return map(root, null)
}

function RowsList({ rows, loading, onPick }: { rows: WhereUsedRow[]; loading: boolean; onPick: (r: WhereUsedRow) => void }) {
  return (
    <div className="flex flex-col gap-3">
      <div className="text-[11px] font-semibold tracking-[0.14em] text-cyan-200/90 uppercase">Uses, level by level</div>
      {loading && [...Array(5).keys()].map((i) => <Skeleton key={i} className="h-16 rounded-xl" />)}
      {rows.map((r, i) => (
        <button key={i} onClick={() => onPick(r)} className="cursor-pointer rounded-xl bg-white/[0.03] px-3 py-2.5 text-left ring-1 ring-white/[0.06] transition ring-inset hover:bg-white/[0.07]">
          <div className="flex items-center gap-2">
            <span className={cn('flex size-5 items-center justify-center rounded-md text-[10.5px] font-bold', r.isDirect ? 'bg-violet-400/20 text-violet-100' : 'bg-white/[0.06] text-ink-300')}>{r.level}</span>
            <Code className="text-[12.5px]">{r.parentItemCode}</Code>
            <StatusChip status={r.status} className="ml-auto" />
          </div>
          <div className="mt-1 truncate font-mono text-[11px] text-ink-400" title={r.pathText}>
            {r.pathText}
          </div>
          <div className="mt-1 flex gap-3 text-[11.5px] text-ink-300 tabular">
            <span>
              {r.bomRef} rev {r.bomRevision}
            </span>
            <span>
              uses {fmtQty(r.usageQty)} {r.uomCode}
            </span>
            <span title={exact(r.qtyPerTopUnit)}>per top unit {fmtQty(r.qtyPerTopUnit, true)}</span>
          </div>
        </button>
      ))}
    </div>
  )
}

function UseDetail({ node, onExplode, onBill }: { node: UseNode; onExplode: (code: string) => void; onBill: (r: WhereUsedRow) => void }) {
  return (
    <div className="flex flex-col gap-4">
      <div>
        <Badge tone={node.children.length === 0 ? 'pink' : 'violet'}>{node.children.length === 0 ? 'Top of a chain' : `Level ${node.depth}`}</Badge>
        <h2 className="mt-2 font-mono text-lg font-semibold text-ink-50">{node.item}</h2>
        <p className="text-[12.5px] text-ink-400">uses {node.parent?.item} through {node.rows.length === 1 ? 'one bill' : `${node.rows.length} bills`}</p>
      </div>
      {node.rows.map((r, i) => (
        <div key={i} className="rounded-2xl bg-white/[0.04] p-4 ring-1 ring-white/[0.08] ring-inset">
          <div className="flex items-center gap-2">
            <Code>{r.bomRef}</Code>
            <span className="text-[11.5px] text-ink-400">rev {r.bomRevision}</span>
            <StatusChip status={r.status} className="ml-auto" />
          </div>
          <div className="mt-3 grid grid-cols-2 gap-3 text-[12.5px]">
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Usage</div>
              <div className="text-ink-50 tabular">
                {fmtQty(r.usageQty)} {r.uomCode} · {r.usageBasis}
              </div>
            </div>
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Per top unit</div>
              <div className="text-ink-50 tabular" title={exact(r.qtyPerTopUnit)}>
                {fmtQty(r.qtyPerTopUnit)}
              </div>
            </div>
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Effective</div>
              <div className="text-ink-200">
                {fmtDate(r.effectiveFrom)} → {r.effectiveTo ? fmtDate(r.effectiveTo) : 'open'}
              </div>
            </div>
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Purpose</div>
              <div className="text-ink-200">
                {r.purpose}
                {r.isOptional ? ' · optional' : ''}
                {r.alternateGroup ? ` · alt ${r.alternateGroup}` : ''}
              </div>
            </div>
          </div>
          <p className="mt-3 font-mono text-[11px] text-ink-400">{r.pathText}</p>
          <Button size="sm" className="mt-3" icon={<ArrowUpRight className="size-3.5" />} onClick={() => onBill(r)}>
            Open bill
          </Button>
        </div>
      ))}
      <Button icon={<Network className="size-4" />} onClick={() => onExplode(node.item)}>
        Explode {node.item}
      </Button>
    </div>
  )
}
