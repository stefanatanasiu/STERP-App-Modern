import { AnimatePresence, motion } from 'motion/react'
import {
  Bike,
  Box,
  Cake,
  ChevronsDownUp,
  ChevronsUpDown,
  Crosshair,
  Layers3,
  ListTree,
  Network,
  Orbit,
  Pause,
  Play,
  Search,
  Shapes,
  Sparkles,
  Tag,
  Tags,
  TableProperties,
  X,
} from 'lucide-react'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { useCostedExplosion, useCostSets, useExplosion, useItem, useLookup, useNettedPlan, type ReadingParams } from '@/api/hooks'
import { buildTree, overlayCosts, overlayNet, type TreeNode } from '@/domain/tree'
import { CLASS_META, type NodeClass } from '@/domain/kinds'
import { exact, fmtDate, fmtInt, fmtMoney, fmtQty } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, IconButton, Skeleton, StatusChip, cn } from '@/components/ui/primitives'
import { DateInput, Field, NumberInput, Segmented, TextInput } from '@/components/ui/forms'
import { EmptyState, Stat } from '@/components/ui/surfaces'
import { OutcomeBanner, ProblemCard } from '@/components/ui/feedback'
import { CostSetSelect, ItemPicker } from '@/components/bom/Pickers'
import { ExplosionTable } from '@/components/bom/ExplosionTable'
import { ExplosionSummary, OccurrenceInspector, Section } from '@/components/bom/ExplosionInspector'
import { toViz, type ExplosionMode } from '@/components/bom/explosionViz'
import { productKindOf } from '@/components/bom/studioModel'
import { Tree3D, type LabelMode } from '@/three/tree/Tree3D'
import type { LayoutKind } from '@/three/tree/layout'

export default function ExplosionPage() {
  const { item: itemParam } = useParams()
  const [search, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const session = useSession((s) => s.session)!
  const item = (itemParam ?? '').trim().toUpperCase()
  const quantity = Number(search.get('qty') ?? '1') || 1
  const revision = (search.get('rev') ?? '').toUpperCase()
  const asOf = search.get('asOf') ?? session.businessDate
  const costSet = search.get('costSet') ?? 'BOMDEMO'
  const mode = (search.get('mode') ?? 'structure') as ExplosionMode
  const layout = (search.get('layout') ?? 'cone') as LayoutKind

  const update = useCallback(
    (patch: Record<string, string | null>) => {
      const next = new URLSearchParams(search)
      for (const [k, v] of Object.entries(patch)) {
        if (v === null || v === '') next.delete(k)
        else next.set(k, v)
      }
      setSearch(next, { replace: true })
    },
    [search, setSearch],
  )

  const params: ReadingParams | null = item ? { item, revision, quantity, asOf, costSet } : null
  const explosion = useExplosion(params)
  const costed = useCostedExplosion(params)
  const netted = useNettedPlan(params, mode === 'net')
  const leaf = useExplosion(mode === 'materials' ? params : null, 'LEAF')
  const sets = useCostSets()
  const currency = sets.data?.find((s) => s.costSet === costSet)?.currency ?? session.selected?.baseCurrency ?? 'GBP'
  const rootItem = useItem(item || undefined)

  const tree = useMemo(() => {
    if (!explosion.data) return null
    const t = buildTree(explosion.data.data.lines)
    if (!t) return null
    if (costed.data) overlayCosts(t, costed.data.data.lines)
    if (netted.data && mode === 'net') overlayNet(t, netted.data.data.lines)
    return t
  }, [explosion.data, costed.data, netted.data, mode])
  const viz = useMemo(() => (tree ? toViz(tree, mode, currency) : null), [tree, mode, currency])

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [labels, setLabels] = useState<LabelMode>('auto')
  const [autoRotate, setAutoRotate] = useState(true)
  const [fitToken, setFitToken] = useState(0)
  const [tableOpen, setTableOpen] = useState(false)
  const [find, setFind] = useState('')
  const [material, setMaterial] = useState<string | null>(null)

  useEffect(() => {
    setSelectedId(null)
    setCollapsed(new Set())
    setMaterial(null)
    setFitToken((t) => t + 1)
  }, [item, quantity, revision, asOf])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setSelectedId(null)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const matches = useMemo(() => {
    if (!tree) return undefined
    const q = find.trim().toLowerCase()
    const s = new Set<string>()
    for (const n of tree.nodes) {
      if (material && n.line.item === material) s.add(n.id)
      else if (q && (n.line.item.toLowerCase().includes(q) || n.line.description.toLowerCase().includes(q))) s.add(n.id)
    }
    return s
  }, [tree, find, material])

  const select = useCallback(
    (id: string | null) => {
      setSelectedId(id)
      if (!id || !tree) return
      // unfold the ancestry of an occurrence chosen in the table or the breadcrumbs
      setCollapsed((c) => {
        let changed = false
        const next = new Set(c)
        for (let p = tree.byId.get(id)?.parent; p; p = p.parent) if (next.delete(p.id)) changed = true
        return changed ? next : c
      })
    },
    [tree],
  )
  const toggle = useCallback((id: string) => {
    setCollapsed((c) => {
      const next = new Set(c)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])
  const collapseAll = () => tree && setCollapsed(new Set(tree.nodes.filter((n) => n.depth >= 1 && n.children.length > 0).map((n) => n.id)))

  const selected: TreeNode | null = (selectedId && tree?.byId.get(selectedId)) || null
  const productKind = productKindOf(rootItem.data?.category)
  const error = explosion.error

  return (
    <div className="flex h-full min-h-[680px] flex-col gap-4 p-5">
      <ParamBar
        key={`${item}|${quantity}|${revision}|${asOf}|${costSet}`}
        item={item}
        quantity={quantity}
        revision={revision}
        asOf={asOf}
        costSet={costSet}
        loading={explosion.isFetching}
        onApply={(p) => {
          const q = new URLSearchParams(search)
          q.set('qty', String(p.quantity))
          if (p.revision) q.set('rev', p.revision)
          else q.delete('rev')
          q.set('asOf', p.asOf)
          q.set('costSet', p.costSet)
          navigate(`/explode/${encodeURIComponent(p.item)}?${q.toString()}`)
        }}
        mode={mode}
        onMode={(m) => update({ mode: m === 'structure' ? null : m })}
      />

      {!item ? (
        <Welcome onPick={(code) => navigate(`/explode/${encodeURIComponent(code)}`)} />
      ) : (
        <>
          <SummaryStrip explosion={explosion} costed={costed} netted={netted} mode={mode} currency={currency} quantity={quantity} />
          <div className="flex min-h-0 flex-1 gap-4">
            <div className="glass relative min-w-0 flex-1 overflow-hidden rounded-3xl">
              {error ? (
                <div className="flex h-full items-center justify-center p-8">
                  <div className="w-full max-w-xl">
                    <ProblemCard error={error} />
                    <p className="mt-4 text-center text-[13px] text-ink-400">The explosion engine refused this request; this is its own answer, unchanged.</p>
                  </div>
                </div>
              ) : !viz || !tree ? (
                <CanvasLoading />
              ) : (
                <>
                  <Tree3D
                    root={viz}
                    layout={layout}
                    collapsed={collapsed}
                    selectedId={selectedId}
                    onSelect={select}
                    onToggle={toggle}
                    labels={labels}
                    autoRotate={autoRotate && !selectedId}
                    matches={matches}
                    fitToken={fitToken}
                  />
                  <CanvasToolbar
                    layout={layout}
                    onLayout={(l) => update({ layout: l === 'cone' ? null : l })}
                    labels={labels}
                    onLabels={setLabels}
                    autoRotate={autoRotate}
                    onAutoRotate={setAutoRotate}
                    onFit={() => {
                      setSelectedId(null)
                      setFitToken((t) => t + 1)
                    }}
                    onCollapseAll={collapseAll}
                    onExpandAll={() => setCollapsed(new Set())}
                    find={find}
                    onFind={setFind}
                    matchCount={matches?.size ?? 0}
                  />
                  <Legend mode={mode} />
                  <button
                    onClick={() => setTableOpen((o) => !o)}
                    className={cn(
                      'glass-strong absolute right-4 bottom-4 z-10 flex h-9 cursor-pointer items-center gap-2 rounded-xl px-3 text-[12.5px] font-medium text-ink-100 transition hover:text-white',
                      tableOpen && 'ring-1 ring-violet-300/40',
                    )}
                  >
                    <TableProperties className="size-4 text-violet-300" /> {tableOpen ? 'Hide table' : 'Indented table'}
                  </button>
                  <AnimatePresence>
                    {tableOpen && (
                      <motion.div
                        initial={{ y: '100%' }}
                        animate={{ y: 0 }}
                        exit={{ y: '100%' }}
                        transition={{ type: 'spring', stiffness: 260, damping: 32 }}
                        className="glass-strong absolute inset-x-3 bottom-16 z-10 h-[42%] overflow-auto rounded-2xl"
                      >
                        <ExplosionTable tree={tree} mode={mode} collapsed={collapsed} selectedId={selectedId} onSelect={select} onToggle={toggle} currency={currency} />
                      </motion.div>
                    )}
                  </AnimatePresence>
                </>
              )}
            </div>
            <aside className="glass w-[380px] shrink-0 overflow-y-auto rounded-3xl p-5">
              {mode === 'materials' ? (
                <MaterialsList query={leaf} selected={material} onPick={(code) => setMaterial((m) => (m === code ? null : code))} quantity={quantity} />
              ) : selected ? (
                <OccurrenceInspector node={selected} asOf={asOf} currency={currency} onSelect={select} />
              ) : tree ? (
                <ExplosionSummary
                  tree={tree}
                  rootLine={tree.root.line}
                  onStudio={() => navigate(`/studio/${encodeURIComponent(item)}?qty=${quantity}&costSet=${encodeURIComponent(costSet)}&asOf=${asOf}`)}
                  studioIcon={productKind === 'bicycle' ? <Bike className="size-4" /> : undefined}
                  extra={
                    productKind === 'cake' ? (
                      <div className="flex items-center gap-2 rounded-xl bg-pink-400/[0.07] px-3 py-2 text-[12px] text-pink-100 ring-1 ring-pink-300/20 ring-inset">
                        <Cake className="size-4 text-pink-300" /> A bakery product: the studio renders it as a cake.
                      </div>
                    ) : productKind === 'bicycle' ? (
                      <div className="flex items-center gap-2 rounded-xl bg-cyan-400/[0.07] px-3 py-2 text-[12px] text-cyan-100 ring-1 ring-cyan-300/20 ring-inset">
                        <Bike className="size-4 text-cyan-300" /> A bicycle: the bike atelier lays every part out and builds the bike from it.
                      </div>
                    ) : null
                  }
                />
              ) : error ? null : (
                <div className="flex flex-col gap-3">
                  <Skeleton className="h-6 w-40" />
                  <Skeleton className="h-4 w-64" />
                  <Skeleton className="mt-4 h-32" />
                </div>
              )}
            </aside>
          </div>
        </>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- parameters

interface Params {
  item: string
  quantity: number
  revision: string
  asOf: string
  costSet: string
}

function ParamBar(props: Params & { loading: boolean; onApply: (p: Params) => void; mode: ExplosionMode; onMode: (m: ExplosionMode) => void }) {
  const [p, setP] = useState<Params>({ item: props.item, quantity: props.quantity, revision: props.revision, asOf: props.asOf, costSet: props.costSet })
  const apply = (next: Partial<Params> = {}) => {
    const v = { ...p, ...next }
    if (!v.item) return
    props.onApply({ ...v, quantity: v.quantity > 0 ? v.quantity : 1 })
  }
  return (
    <div className="glass relative z-20 flex flex-wrap items-end gap-3 rounded-3xl px-5 py-4">
      <div className="mr-2 flex items-center gap-3 self-center">
        <div className="flex size-10 items-center justify-center rounded-2xl bg-[linear-gradient(135deg,rgba(139,92,246,0.35),rgba(6,182,212,0.25))] ring-1 ring-white/15">
          <Network className="size-5 text-violet-100" />
        </div>
        <div className="hidden min-[1900px]:block">
          <div className="text-[15px] font-semibold tracking-tight text-ink-50">Explosion</div>
          <div className="text-[11.5px] text-ink-400">bom_explodeBill · bom_costBill · bom_netBillRequirement</div>
        </div>
      </div>
      <Field label="Parent item" className="w-[260px]">
        <ItemPicker value={p.item} onChange={(item) => (setP({ ...p, item }), apply({ item }))} />
      </Field>
      <Field label="Revision" className="w-[84px]">
        <TextInput value={p.revision} placeholder="auto" maxLength={4} onChange={(e) => setP({ ...p, revision: e.target.value.toUpperCase() })} onKeyDown={(e) => e.key === 'Enter' && apply()} />
      </Field>
      <Field label="Quantity" className="w-[104px]">
        <NumberInput min={0} step="any" value={p.quantity} onChange={(e) => setP({ ...p, quantity: Number(e.target.value) })} onKeyDown={(e) => e.key === 'Enter' && apply()} />
      </Field>
      <Field label="As at" className="w-[150px]">
        <DateInput value={p.asOf} onChange={(e) => setP({ ...p, asOf: e.target.value })} />
      </Field>
      <Field label="Cost set" className="w-[200px]">
        <CostSetSelect value={p.costSet} onChange={(costSet) => (setP({ ...p, costSet }), apply({ costSet }))} />
      </Field>
      <Button variant="primary" loading={props.loading} onClick={() => apply()} icon={<Sparkles className="size-4" />}>
        Explode
      </Button>
      <div className="flex-1" />
      <Segmented<ExplosionMode>
        value={props.mode}
        onChange={props.onMode}
        options={[
          { value: 'structure', label: 'Structure', icon: <ListTree className="size-3.5" /> },
          { value: 'cost', label: 'Cost', icon: <Tag className="size-3.5" /> },
          { value: 'net', label: 'Netting', icon: <Layers3 className="size-3.5" /> },
          { value: 'materials', label: 'Materials', icon: <Box className="size-3.5" /> },
        ]}
      />
    </div>
  )
}

// ---------------------------------------------------------------- summary of the API answers

function SummaryStrip({
  explosion,
  costed,
  netted,
  mode,
  currency,
  quantity,
}: {
  explosion: ReturnType<typeof useExplosion>
  costed: ReturnType<typeof useCostedExplosion>
  netted: ReturnType<typeof useNettedPlan>
  mode: ExplosionMode
  currency: string
  quantity: number
}) {
  const e = explosion.data?.data
  const c = costed.data?.data
  const n = netted.data?.data
  if (explosion.isPending && !e) {
    return (
      <div className="grid grid-cols-6 gap-3">
        {[...Array(6).keys()].map((i) => (
          <Skeleton key={i} className="h-[74px] rounded-2xl" />
        ))}
      </div>
    )
  }
  if (!e) return null
  return (
    <div className="flex flex-col gap-2">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Stat label="Completeness" value={<StatusChip status={e.completeness} className="text-[12px]" />} hint={e.stopSummary.replace('Stopped: ', 'stops: ')} title={e.stopSummary} accent="#a78bfa" />
        <Stat label="Occurrences" value={fmtInt(e.occurrences)} hint={`depth ${e.depthReached} below the product`} accent="#22d3ee" />
        {mode === 'net' && n ? (
          <>
            <Stat label="Shortages" value={<span className={n.shortages > 0 ? 'text-rose-300' : ''}>{fmtInt(n.shortages)}</span>} hint={`netted plan ${n.completeness.toLowerCase()}`} accent="#fb7185" />
            <Stat label="Max buildable" value={fmtQty(n.maxBuildable)} hint="as the engine reports it" accent="#34d399" />
          </>
        ) : (
          <>
            <Stat label={`Total cost · ${fmtQty(quantity)}`} value={c ? fmtMoney(c.totalCost, currency) : '–'} title={exact(c?.totalCost)} hint={c ? `${c.completeness.toLowerCase()} · ${c.missingRates} missing rate(s)` : costed.error ? 'cost not available' : 'costing…'} accent="#f9a8d4" />
            <Stat label="Unit cost" value={c ? fmtMoney(c.unitCost, currency) : '–'} title={exact(c?.unitCost)} hint="per unit of the product" accent="#fbbf24" />
          </>
        )}
        <Stat label="Material" value={c ? fmtMoney(c.materialCost, currency) : '–'} title={exact(c?.materialCost)} hint="bom_costBill" accent="#22d3ee" />
        <Stat
          label="Conversion · recovery"
          value={c ? fmtMoney(c.conversionCost, currency) : '–'}
          title={exact(c?.conversionCost)}
          hint={c ? `recovery credit ${fmtMoney(c.recoveryCredit, currency)}` : 'bom_costBill'}
          accent="#34d399"
        />
      </div>
      <OutcomeBanner outcome={explosion.data?.outcome} />
      {mode === 'net' && <OutcomeBanner outcome={netted.data?.outcome} />}
      {mode === 'net' && netted.error && <ProblemCard error={netted.error} />}
    </div>
  )
}

// ---------------------------------------------------------------- canvas overlays

function CanvasToolbar(props: {
  layout: LayoutKind
  onLayout: (l: LayoutKind) => void
  labels: LabelMode
  onLabels: (l: LabelMode) => void
  autoRotate: boolean
  onAutoRotate: (v: boolean) => void
  onFit: () => void
  onCollapseAll: () => void
  onExpandAll: () => void
  find: string
  onFind: (v: string) => void
  matchCount: number
}) {
  return (
    <div className="absolute top-4 left-4 z-10 flex flex-wrap items-center gap-2">
      <Segmented<LayoutKind>
        size="sm"
        className="glass-strong"
        value={props.layout}
        onChange={props.onLayout}
        options={[
          { value: 'cone', label: 'Cone', icon: <Network className="size-3.5" />, title: 'Cone tree: children on a circle below their parent' },
          { value: 'orbit', label: 'Orbit', icon: <Orbit className="size-3.5" />, title: 'Orbit: levels on rings around the product' },
          { value: 'tiers', label: 'Tiers', icon: <Layers3 className="size-3.5" />, title: 'Tiers: a tidy top-down tree' },
        ]}
      />
      <div className="glass-strong flex items-center gap-0.5 rounded-xl p-1">
        <IconButton label={props.autoRotate ? 'Stop rotating' : 'Rotate'} onClick={() => props.onAutoRotate(!props.autoRotate)} active={props.autoRotate} className="size-7">
          {props.autoRotate ? <Pause className="size-3.5" /> : <Play className="size-3.5" />}
        </IconButton>
        <IconButton label="Fit the whole tree" onClick={props.onFit} className="size-7">
          <Crosshair className="size-3.5" />
        </IconButton>
        <IconButton label="Fold every made item" onClick={props.onCollapseAll} className="size-7">
          <ChevronsDownUp className="size-3.5" />
        </IconButton>
        <IconButton label="Unfold everything" onClick={props.onExpandAll} className="size-7">
          <ChevronsUpDown className="size-3.5" />
        </IconButton>
        <IconButton
          label={`Labels: ${props.labels}`}
          onClick={() => props.onLabels(props.labels === 'auto' ? 'all' : props.labels === 'all' ? 'none' : 'auto')}
          active={props.labels !== 'auto'}
          className="size-7"
        >
          {props.labels === 'none' ? <Shapes className="size-3.5" /> : props.labels === 'all' ? <Tags className="size-3.5" /> : <Tag className="size-3.5" />}
        </IconButton>
      </div>
      <div className="glass-strong flex h-9 items-center gap-2 rounded-xl px-2.5">
        <Search className="size-3.5 text-ink-400" />
        <input
          value={props.find}
          onChange={(e) => props.onFind(e.target.value)}
          placeholder="Find in tree"
          className="w-32 bg-transparent text-[12.5px] text-ink-50 outline-none placeholder:text-ink-500"
        />
        {props.find && (
          <>
            <span className="text-[11px] text-ink-400 tabular">{props.matchCount}</span>
            <button onClick={() => props.onFind('')} className="cursor-pointer text-ink-400 hover:text-ink-100" aria-label="Clear">
              <X className="size-3.5" />
            </button>
          </>
        )}
      </div>
    </div>
  )
}

function Legend({ mode }: { mode: ExplosionMode }) {
  const classes: NodeClass[] = ['root', 'made', 'bought', 'service', 'phantom', 'byproduct', 'note']
  return (
    <div className="glass-strong absolute bottom-4 left-4 z-10 flex max-w-[calc(100%-200px)] flex-wrap items-center gap-x-4 gap-y-1.5 rounded-2xl px-4 py-2.5">
      {classes.map((c) => (
        <span key={c} className="flex items-center gap-1.5 text-[11.5px] text-ink-200" title={CLASS_META[c].hint}>
          <span className="size-2 rounded-full" style={{ background: CLASS_META[c].color, boxShadow: `0 0 8px ${CLASS_META[c].color}` }} />
          {CLASS_META[c].label}
        </span>
      ))}
      {mode === 'cost' && <span className="text-[11.5px] text-ink-400">size = extended cost (API)</span>}
      {mode === 'net' && (
        <>
          <span className="flex items-center gap-1.5 text-[11.5px] text-rose-200">
            <span className="size-2 rounded-full ring-2 ring-rose-400" /> shortage
          </span>
          <span className="flex items-center gap-1.5 text-[11.5px] text-emerald-200">
            <span className="size-2 rounded-full ring-2 ring-emerald-400" /> covered from stock
          </span>
        </>
      )}
    </div>
  )
}

function CanvasLoading() {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4">
      <div className="relative size-16">
        <span className="absolute inset-0 animate-pulse-ring rounded-full bg-violet-400/30" />
        <span className="absolute inset-0 animate-pulse-ring rounded-full bg-cyan-400/20 [animation-delay:0.8s]" />
        <span className="absolute inset-4 rounded-full bg-[linear-gradient(135deg,#8b5cf6,#06b6d4)] shadow-[0_0_40px_rgba(139,92,246,0.8)]" />
      </div>
      <p className="text-sm text-ink-300">The explosion engine is walking the structure…</p>
    </div>
  )
}

// ---------------------------------------------------------------- materials (LEAF reading)

function MaterialsList({
  query,
  selected,
  onPick,
  quantity,
}: {
  query: ReturnType<typeof useExplosion>
  selected: string | null
  onPick: (code: string) => void
  quantity: number
}) {
  if (query.error) return <ProblemCard error={query.error} />
  const lines = query.data?.data.lines ?? []
  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className="text-[11px] font-semibold tracking-[0.14em] text-violet-300/90 uppercase">Materials</div>
        <h2 className="mt-1 text-lg font-semibold text-ink-50">Everything to buy for {fmtQty(quantity)}</h2>
        <p className="mt-1 text-[12.5px] leading-relaxed text-ink-400">
          The engine's leaf reading (bom_explodeBill, view LEAF): each purchased material once, its requirement summed by the engine across every place it is used.
        </p>
      </div>
      <OutcomeBanner outcome={query.data?.outcome} />
      <Section title={`${lines.length} materials`} hint="view LEAF">
        <div className="flex flex-col gap-1.5">
          {query.isPending &&
            [...Array(8).keys()].map((i) => <Skeleton key={i} className="h-12 rounded-xl" />)}
          {lines.map((l) => {
            const active = selected === l.item
            return (
              <button
                key={l.item}
                onClick={() => onPick(l.item)}
                className={cn(
                  'flex cursor-pointer items-center gap-3 rounded-xl px-3 py-2.5 text-left ring-1 transition ring-inset',
                  active ? 'bg-cyan-400/12 ring-cyan-300/40' : 'bg-white/[0.03] ring-white/[0.06] hover:bg-white/[0.06]',
                )}
              >
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-mono text-[12.5px] text-ink-50">{l.item}</span>
                  <span className="block truncate text-[11.5px] text-ink-400">{l.description}</span>
                </span>
                <span className="text-right">
                  <span className="block text-[14px] font-semibold text-ink-50 tabular" title={exact(l.required)}>
                    {fmtQty(l.required)}
                  </span>
                  <span className="block text-[10.5px] text-ink-400">{l.unit}</span>
                </span>
              </button>
            )
          })}
        </div>
      </Section>
      {selected && <p className="text-[12px] text-cyan-200">Every occurrence of {selected} glows in the tree.</p>}
    </div>
  )
}

// ---------------------------------------------------------------- start

function Welcome({ onPick }: { onPick: (code: string) => void }) {
  const bills = useLookup('BOM.HEADER', 'FG-')
  const businessDate = useSession((s) => s.session?.businessDate)
  const featured = (bills.data?.items ?? []).filter((r) => String(r.ParentItemCode).startsWith('FG-') && r.Status === 'RELEASED')
  return (
    <div className="glass flex flex-1 flex-col items-center justify-center rounded-3xl p-10">
      <EmptyState icon={<Network className="size-6" />} title="Explode a product into everything it is made of">
        Pick a parent item above, or start from one of the released finished goods. The structure, costs and netting come straight from the STERP
        explosion engine; GUIv2 draws them as an interactive 3D tree.
      </EmptyState>
      <div className="mt-2 grid w-full max-w-3xl grid-cols-1 gap-3 md:grid-cols-2">
        {featured.map((r) => (
          <button
            key={`${r.BomRef}-${r.BomRevision}`}
            onClick={() => onPick(String(r.ParentItemCode))}
            className="group flex cursor-pointer items-center gap-4 rounded-2xl bg-white/[0.04] px-4 py-3.5 text-left ring-1 ring-white/[0.08] transition hover:bg-white/[0.07] hover:ring-violet-300/30 ring-inset"
          >
            <span className="flex size-11 items-center justify-center rounded-xl bg-[linear-gradient(135deg,rgba(139,92,246,0.3),rgba(6,182,212,0.2))] ring-1 ring-white/10">
              {String(r.ParentItemCode).includes('CAKE') ? <Cake className="size-5 text-pink-200" /> : <Network className="size-5 text-violet-100" />}
            </span>
            <span className="min-w-0 flex-1">
              <span className="block font-mono text-[13px] text-ink-50">{String(r.ParentItemCode)}</span>
              <span className="block truncate text-[12px] text-ink-400">{String(r.Description ?? '')}</span>
            </span>
            <Badge tone="violet">{String(r.BomRef)}</Badge>
          </button>
        ))}
      </div>
      <p className="mt-6 text-[11.5px] text-ink-500">As at {fmtDate(businessDate)} · bills from the BOM.HEADER lookup</p>
    </div>
  )
}
