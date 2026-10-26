import { AnimatePresence, motion } from 'motion/react'
import { ArrowLeft, Bike, Cake, Eye, Flame, Info, Layers, Lightbulb, Network, Package, Pause, Play, Scissors, Sparkles, Sun, X } from 'lucide-react'
import { lazy, Suspense, useMemo, useState, type ReactNode } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { useBillComponents, useBillRevisions, useCostedExplosion, useCostSets, useExplosion, useItem, type ReadingParams } from '@/api/hooks'
import { buildTree, overlayCosts } from '@/domain/tree'
import { exact, fmtMoney, fmtQty, fmtRate } from '@/domain/format'
import { useSession } from '@/state/session'
import { buildStudioModel, costsOf, PART_COLOR, partOrderOf, productKindOf, subPartsOf, type PartKey, type StudioMaterial, type StudioModel } from '@/components/bom/studioModel'
import { Badge, Button, Code, IconButton, Skeleton, cn } from '@/components/ui/primitives'
import { NumberInput, Segmented } from '@/components/ui/forms'
import { EmptyState } from '@/components/ui/surfaces'
import { OutcomeBanner, ProblemCard } from '@/components/ui/feedback'
import { ItemPicker } from '@/components/bom/Pickers'
import { StudioScene, type Lighting, type StudioSelection } from '@/three/studio/StudioScene'
import type { ProductView } from '@/three/studio/types'

// Bicycles have their own studio, the bike atelier: a 2.5D flat lay of every line of the explosion that builds itself into
// the bike. Cakes (and anything else) keep the 3D product studio below.
const BikeStudio = lazy(() => import('@/atelier/BikeStudio'))

const BATCHES = [1, 6, 12, 24]

/** Words of the studio per kind of product. */
const WORDS: Record<StudioModel['kind'], { unit: string; materials: string; sheet: string; hint: string }> = {
  cake: { unit: 'cake', materials: 'Ingredients', sheet: 'Recipe', hint: 'Click an ingredient to see where it goes, or a part of the cake to see what it is made of.' },
  generic: { unit: 'unit', materials: 'Materials', sheet: 'Materials', hint: 'Click a material to see its requirement and cost.' },
}

export default function StudioPage() {
  const { item: itemParam } = useParams()
  const [search, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const session = useSession((s) => s.session)!
  const item = (itemParam ?? '').trim().toUpperCase()
  const quantity = Number(search.get('qty') ?? '1') || 1
  const asOf = search.get('asOf') ?? session.businessDate
  const costSet = search.get('costSet') ?? 'BOMDEMO'

  const params: ReadingParams | null = item ? { item, revision: '', quantity, asOf, costSet } : null
  const explosion = useExplosion(params)
  const leaf = useExplosion(params, 'LEAF')
  const costed = useCostedExplosion(params)
  const master = useItem(item || undefined)
  const sets = useCostSets()
  const currency = sets.data?.find((s) => s.costSet === costSet)?.currency ?? session.selected?.baseCurrency ?? 'GBP'
  const bicycle = !master.isPending && productKindOf(master.data?.category) === 'bicycle'

  const tree = useMemo(() => {
    if (!explosion.data) return null
    const t = buildTree(explosion.data.data.lines)
    if (t && costed.data) overlayCosts(t, costed.data.data.lines)
    return t
  }, [explosion.data, costed.data])

  const model = useMemo<StudioModel | null>(() => {
    if (!tree || !leaf.data || master.isPending || bicycle) return null
    return buildStudioModel(tree, leaf.data.data.lines, master.data?.category)
  }, [tree, leaf.data, master.data, master.isPending, bicycle])

  const [view, setView] = useState<ProductView>('assembled')
  const [lighting, setLighting] = useState<Lighting>('studio')
  const [showIngredients, setShowIngredients] = useState(true)
  const [showBox, setShowBox] = useState(false)
  const [autoRotate, setAutoRotate] = useState(true)
  const [selection, setSelection] = useState<StudioSelection | null>(null)
  const [fitToken, setFitToken] = useState(0)

  const setQuantity = (q: number) => {
    const next = new URLSearchParams(search)
    next.set('qty', String(q))
    setSearch(next, { replace: true })
  }

  if (!item) return <StudioStart onPick={(code) => navigate(`/studio/${encodeURIComponent(code)}`)} />

  const error = explosion.error ?? leaf.error
  const back = () => navigate(`/explode/${encodeURIComponent(item)}?qty=${quantity}&costSet=${encodeURIComponent(costSet)}&asOf=${asOf}`)

  if (bicycle && !error) {
    if (!tree || !leaf.data) return <StudioLoading />
    return (
      <Suspense fallback={<StudioLoading />}>
        <BikeStudio
          item={item}
          tree={tree}
          leaf={leaf.data.data.lines}
          costed={costed}
          category={master.data?.category}
          quantity={quantity}
          onQuantity={setQuantity}
          asOf={asOf}
          currency={currency}
          onBack={back}
          banner={explosion.data?.outcome && explosion.data.outcome.returnCode !== 0 ? <OutcomeBanner outcome={explosion.data.outcome} /> : undefined}
        />
      </Suspense>
    )
  }

  const kind: StudioModel['kind'] = model?.kind ?? (productKindOf(master.data?.category) === 'cake' ? 'cake' : 'generic')
  const words = WORDS[kind]
  const partFigure = (p: PartKey) => {
    const n = model?.parts.get(p)?.node
    return n ? `${fmtQty(n.line.required, true)} ${n.line.unit}` : ''
  }

  return (
    <div className="relative h-full min-h-[680px] overflow-hidden">
      {error ? (
        <div className="flex h-full items-center justify-center p-8">
          <div className="w-full max-w-xl">
            <ProblemCard error={error} />
          </div>
        </div>
      ) : !model ? (
        <StudioLoading />
      ) : (
        <StudioScene
          model={model}
          view={view}
          showIngredients={showIngredients}
          showBox={showBox}
          lighting={lighting}
          autoRotate={autoRotate}
          selection={selection}
          onSelect={setSelection}
          figure={materialFigure}
          partFigure={partFigure}
          fitToken={fitToken}
        />
      )}

      {/* title and view */}
      <div className="pointer-events-none absolute inset-x-0 top-0 z-20 flex items-start gap-4 p-5">
        <div className="glass-strong pointer-events-auto flex items-center gap-4 rounded-3xl py-3.5 pr-5 pl-4">
          <IconButton label="Back to the explosion" onClick={back}>
            <ArrowLeft className="size-4" />
          </IconButton>
          <div className="flex size-11 items-center justify-center rounded-2xl bg-[linear-gradient(135deg,#fde68a,#f9a8d4_55%,#c4b5fd)] shadow-[0_8px_26px_-8px_rgba(249,168,212,0.8)]">
            {kind === 'cake' ? <Cake className="size-5 text-ink-950" /> : <Sparkles className="size-5 text-ink-950" />}
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              {model ? (
                <h1 className="font-display text-[22px] leading-tight font-semibold tracking-tight text-ink-50 italic">{shortTitle(model.root.line.description)}</h1>
              ) : (
                <Skeleton className="h-6 w-56" />
              )}
              {master.data?.category && <Badge tone="pink">{master.data.category}</Badge>}
            </div>
            <div className="mt-0.5 flex items-center gap-2 text-[12px] text-ink-300">
              <Code className="text-[12px] text-ink-200">{item}</Code>
              <span className="text-ink-500">·</span>
              <span>Product studio · live STERP explosion</span>
            </div>
          </div>
        </div>
        <div className="flex-1" />
        {model && model.kind !== 'generic' && (
          <Segmented<ProductView>
            className="glass-strong pointer-events-auto"
            value={view}
            onChange={setView}
            options={[
              { value: 'assembled', label: 'Assembled', icon: <Cake className="size-3.5" /> },
              { value: 'cutaway', label: 'Cutaway', icon: <Scissors className="size-3.5" /> },
              { value: 'exploded', label: 'Exploded', icon: <Layers className="size-3.5" /> },
            ]}
          />
        )}
      </div>

      {/* batch and cost */}
      <div className="absolute top-[108px] left-5 z-20 w-[300px]">
        <BatchCard quantity={quantity} onQuantity={setQuantity} costed={costed} currency={currency} unit={words.unit} />
      </div>

      {/* the selection or the recipe */}
      <div className="absolute top-[108px] right-5 bottom-5 z-20 w-[370px]">
        <div className="glass-strong h-full overflow-y-auto rounded-3xl p-5">
          {!model ? (
            <div className="flex flex-col gap-3">
              <Skeleton className="h-5 w-40" />
              <Skeleton className="h-24" />
              <Skeleton className="h-40" />
            </div>
          ) : (
            <AnimatePresence mode="wait">
              <motion.div
                key={selection ? JSON.stringify(selection) : 'recipe'}
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: -6 }}
                transition={{ duration: 0.2 }}
              >
                {selection?.kind === 'material' ? (
                  <MaterialDetail
                    model={model}
                    item={selection.item}
                    currency={currency}
                    onClose={() => setSelection(null)}
                    onPart={(p) => setSelection({ kind: 'part', part: p })}
                  />
                ) : selection?.kind === 'part' ? (
                  <PartDetail
                    model={model}
                    part={selection.part}
                    currency={currency}
                    onClose={() => setSelection(null)}
                    onMaterial={(i) => setSelection({ kind: 'material', item: i })}
                    onPart={(q) => setSelection({ kind: 'part', part: q })}
                  />
                ) : (
                  <Recipe
                    model={model}
                    asOf={asOf}
                    onMaterial={(i) => setSelection({ kind: 'material', item: i })}
                    onPart={(p) => setSelection({ kind: 'part', part: p })}
                  />
                )}
              </motion.div>
            </AnimatePresence>
          )}
        </div>
      </div>

      {/* stage controls */}
      <div className="glass-strong absolute bottom-5 left-5 z-20 flex items-center gap-1 rounded-2xl p-1.5">
        <ToolToggle on={showIngredients} onClick={() => setShowIngredients((v) => !v)} icon={<Eye className="size-4" />} label={words.materials} />
        {kind !== 'generic' && <ToolToggle on={showBox} onClick={() => setShowBox((v) => !v)} icon={<Package className="size-4" />} label="Packaging" />}
        <ToolToggle
          on={lighting === 'candle'}
          onClick={() => setLighting((l) => (l === 'candle' ? 'studio' : 'candle'))}
          icon={lighting === 'candle' ? <Flame className="size-4" /> : <Sun className="size-4" />}
          label={lighting === 'candle' ? 'Candlelight' : 'Studio light'}
        />
        <ToolToggle on={autoRotate} onClick={() => setAutoRotate((v) => !v)} icon={autoRotate ? <Pause className="size-4" /> : <Play className="size-4" />} label="Turntable" />
        <span className="mx-1 h-6 w-px bg-white/10" />
        <Button
          size="sm"
          variant="ghost"
          icon={<Lightbulb className="size-3.5" />}
          onClick={() => {
            setSelection(null)
            setFitToken((t) => t + 1)
          }}
        >
          Reset view
        </Button>
      </div>
      {model && !selection && (
        <div className="pointer-events-none absolute bottom-[78px] left-5 z-20 flex items-center gap-2 rounded-xl bg-ink-950/50 px-3 py-1.5 text-[12px] text-ink-200 backdrop-blur">
          <Info className="size-3.5 text-violet-300" /> {words.hint}
        </div>
      )}
      {explosion.data?.outcome && explosion.data.outcome.returnCode !== 0 && (
        <div className="absolute bottom-[78px] left-1/2 z-20 -translate-x-1/2">
          <OutcomeBanner outcome={explosion.data.outcome} />
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- panels

function BatchCard({
  quantity,
  onQuantity,
  costed,
  currency,
  unit,
}: {
  quantity: number
  onQuantity: (q: number) => void
  costed: ReturnType<typeof useCostedExplosion>
  currency: string
  unit: string
}) {
  const c = costed.data?.data
  const [custom, setCustom] = useState('')
  return (
    <div className="glass-strong rounded-3xl p-4">
      <div className="text-[11px] font-semibold tracking-[0.12em] text-ink-300 uppercase">Batch</div>
      <div className="mt-2 flex items-center gap-1.5">
        {BATCHES.map((b) => (
          <button
            key={b}
            onClick={() => onQuantity(b)}
            className={cn(
              'h-8 min-w-10 cursor-pointer rounded-lg px-2.5 text-[13px] font-semibold ring-1 transition tabular ring-inset',
              quantity === b
                ? 'bg-[linear-gradient(135deg,rgba(249,168,212,0.35),rgba(196,181,253,0.3))] text-white ring-pink-200/40'
                : 'bg-white/[0.04] text-ink-200 ring-white/10 hover:bg-white/[0.08]',
            )}
          >
            {b}
          </button>
        ))}
        <NumberInput
          value={custom}
          placeholder="…"
          min={1}
          className="h-8 w-16 px-2 text-[13px]"
          onChange={(e) => setCustom(e.target.value)}
          onKeyDown={(e) => {
            const v = Number(custom)
            if (e.key === 'Enter' && v > 0) onQuantity(v)
          }}
        />
      </div>
      <div className="mt-4 grid grid-cols-2 gap-3">
        <div>
          <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Cost per {unit}</div>
          <div className="mt-0.5 font-display text-[26px] leading-none font-semibold text-gradient-warm tabular" title={exact(c?.unitCost)}>
            {c ? fmtMoney(c.unitCost, currency) : '…'}
          </div>
        </div>
        <div>
          <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Batch of {fmtQty(quantity)}</div>
          <div className="mt-0.5 text-[20px] leading-none font-semibold text-ink-50 tabular" title={exact(c?.totalCost)}>
            {c ? fmtMoney(c.totalCost, currency) : '…'}
          </div>
        </div>
      </div>
      {c && (
        <div className="mt-3 grid grid-cols-3 gap-2 text-[11px]">
          <Mini label="Material" value={fmtMoney(c.materialCost, currency)} />
          <Mini label="Conversion" value={fmtMoney(c.conversionCost, currency)} />
          <Mini label="Recovery" value={fmtMoney(c.recoveryCredit, currency)} />
        </div>
      )}
      {costed.error && <p className="mt-3 text-[12px] text-amber-200">Costing is not available: {(costed.error as Error).message}</p>}
      <p className="mt-3 text-[11px] leading-snug text-ink-400">
        Figures from bom_costBill: fixed per-run lines and set-up are spread over the batch, so the cost per {unit} changes with the batch size.
      </p>
    </div>
  )
}

function Mini({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl bg-white/[0.04] px-2 py-1.5 ring-1 ring-white/[0.06] ring-inset">
      <div className="text-[9.5px] tracking-wide text-ink-400 uppercase">{label}</div>
      <div className="font-semibold text-ink-100 tabular">{value}</div>
    </div>
  )
}

function Recipe({ model, asOf, onMaterial, onPart }: { model: StudioModel; asOf: string; onMaterial: (item: string) => void; onPart: (p: PartKey) => void }) {
  const bills = useBillRevisions({ parentItemCode: model.root.line.item, asOf })
  const bill = bills.data?.find((b) => b.isEffectiveNow) ?? bills.data?.[0]
  const components = useBillComponents(bill?.bomRef, bill?.bomRevision, asOf)
  const instructions = (components.data ?? []).filter((c) => c.lineKind.trim() === 'NOTE' && c.notes)
  const drawn = model.kind !== 'generic'
  const groups = partOrderOf(model.kind)
    .map((p) => ({ part: p, materials: model.materials.filter((m) => !m.byproduct && !m.service && m.feeds[0] === p) }))
    .filter((g) => g.materials.length > 0 || (drawn && model.parts.has(g.part)))
  const extras = model.materials.filter((m) => m.byproduct || m.service)
  return (
    <div className="flex flex-col gap-5">
      <div>
        <div className="text-[11px] font-semibold tracking-[0.14em] text-pink-200/90 uppercase">{WORDS[model.kind].sheet}</div>
        <p className="mt-1 text-[13px] leading-relaxed text-ink-300">{model.root.line.description}</p>
      </div>
      {groups.map((g) => {
        const part = model.parts.get(g.part)
        return (
          <section key={g.part}>
            <button onClick={() => drawn && onPart(g.part)} className="group mb-2 flex w-full cursor-pointer items-center gap-2 text-left">
              <span className="size-2.5 rounded-full" style={{ background: PART_COLOR[g.part], boxShadow: `0 0 10px ${PART_COLOR[g.part]}` }} />
              <span className="text-[13px] font-semibold text-ink-50 group-hover:text-white">{part?.name ?? (g.part === 'core' ? (drawn ? 'Final assembly' : 'Materials') : g.part)}</span>
              {part && (
                <span className="ml-auto text-[11.5px] text-ink-400 tabular">
                  {fmtQty(part.node.line.required, true)} {part.node.line.unit}
                </span>
              )}
            </button>
            <div className="flex flex-wrap gap-1.5">
              {g.materials.map((m) => (
                <button
                  key={m.item}
                  onClick={() => onMaterial(m.item)}
                  className="cursor-pointer rounded-lg bg-white/[0.05] px-2 py-1 text-[11.5px] text-ink-200 ring-1 ring-white/[0.08] transition ring-inset hover:bg-white/[0.1] hover:text-white"
                >
                  {m.name}
                </button>
              ))}
            </div>
          </section>
        )
      })}
      {extras.length > 0 && (
        <section>
          <div className="mb-2 text-[11px] font-semibold tracking-[0.12em] text-ink-300 uppercase">Labour and recovery</div>
          <div className="flex flex-col gap-1.5">
            {extras.map((m) => (
              <button
                key={m.item}
                onClick={() => onMaterial(m.item)}
                className="flex cursor-pointer items-center gap-2 rounded-xl bg-white/[0.04] px-3 py-2 text-left ring-1 ring-white/[0.07] ring-inset hover:bg-white/[0.07]"
              >
                <Badge tone={m.byproduct ? 'amber' : 'emerald'}>{m.byproduct ? 'By-product' : 'Service'}</Badge>
                <span className="truncate text-[12.5px] text-ink-100">{m.name}</span>
                <span className="ml-auto text-[11.5px] text-ink-400 tabular">{materialFigure(m)}</span>
              </button>
            ))}
          </div>
        </section>
      )}
      {instructions.length > 0 && (
        <section className="rounded-2xl bg-[linear-gradient(135deg,rgba(253,230,138,0.08),rgba(249,168,212,0.06))] p-4 ring-1 ring-amber-200/15 ring-inset">
          <div className="mb-1.5 text-[11px] font-semibold tracking-[0.12em] text-amber-100 uppercase">Work instruction</div>
          {instructions.map((c) => (
            <p key={c.lineNum} className="font-display text-[15px] leading-snug text-ink-50 italic">
              “{c.notes}”
            </p>
          ))}
          <p className="mt-2 font-mono text-[10.5px] text-ink-400">
            {bill?.bomRef} line {instructions[0].lineNum} · bom_listBillComponents
          </p>
        </section>
      )}
    </div>
  )
}

function MaterialDetail({
  model,
  item,
  currency,
  onClose,
  onPart,
}: {
  model: StudioModel
  item: string
  currency: string
  onClose: () => void
  onPart: (p: PartKey) => void
}) {
  const m = model.materials.find((x) => x.item === item)
  const master = useItem(item)
  if (!m) return null
  const costs = costsOf(m)
  return (
    <div className="flex flex-col gap-5">
      <Header
        title={m.name}
        code={m.item}
        onClose={onClose}
        tone={m.byproduct ? 'amber' : m.service ? 'emerald' : 'cyan'}
        kind={m.byproduct ? 'By-product' : m.service ? 'Service' : 'Material'}
      />
      <p className="-mt-2 text-[13px] text-ink-300">{m.description}</p>
      <div className="rounded-2xl bg-white/[0.04] p-4 ring-1 ring-white/[0.08] ring-inset">
        <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">{m.byproduct ? 'Recovered in this batch' : 'Needed for this batch'}</div>
        <div className="mt-1 font-display text-[30px] leading-none font-semibold text-ink-50 tabular">{materialFigure(m)}</div>
        <div className="mt-1.5 text-[11px] text-ink-400">
          {m.leaf ? 'the engine’s leaf requirement, summed over every use (view LEAF)' : 'the by-product line of the explosion'}
        </div>
      </div>
      {m.feeds.length > 0 && model.kind !== 'generic' && (
        <section>
          <SectionTitle>{m.byproduct ? 'Comes out of' : 'Goes into'}</SectionTitle>
          <div className="flex flex-wrap gap-2">
            {m.feeds.map((p) => (
              <button
                key={p}
                onClick={() => onPart(p)}
                className="flex cursor-pointer items-center gap-2 rounded-xl bg-white/[0.05] px-3 py-1.5 text-[12.5px] text-ink-100 ring-1 ring-white/10 ring-inset hover:bg-white/[0.09]"
              >
                <span className="size-2 rounded-full" style={{ background: PART_COLOR[p] }} />
                {model.parts.get(p)?.name ?? (p === 'core' ? 'Final assembly' : p)}
              </button>
            ))}
          </div>
        </section>
      )}
      <section>
        <SectionTitle>Occurrences in the explosion</SectionTitle>
        <div className="flex flex-col gap-2">
          {m.occurrences.map((o) => {
            const cost = o.cost
            return (
              <div key={o.id} className="rounded-xl bg-white/[0.03] px-3 py-2.5 ring-1 ring-white/[0.06] ring-inset">
                <div className="flex items-center gap-2 text-[11.5px] text-ink-400">
                  in <Code className="text-[11.5px] text-ink-200">{o.parent?.line.item}</Code>
                </div>
                <div className="mt-1 flex items-baseline justify-between gap-2">
                  <span className="text-[13px] text-ink-100 tabular" title={exact(o.line.required)}>
                    {fmtQty(o.line.required)} {o.line.unit}
                    <span className="ml-1.5 text-[11px] text-ink-500">usage {fmtQty(o.line.usage)}</span>
                  </span>
                  {cost && (
                    <span className="text-[13px] font-semibold text-ink-50 tabular" title={exact(cost.extendedCost)}>
                      {fmtMoney(cost.extendedCost, currency)}
                    </span>
                  )}
                </div>
                {cost && (
                  <div className="mt-0.5 text-[11px] text-ink-500">
                    {fmtRate(cost.unitCost, currency)} / {cost.unit} · {cost.element} · {cost.source}
                  </div>
                )}
              </div>
            )
          })}
        </div>
        {costs.length > 0 && <p className="mt-2 text-[11px] text-ink-500">Costed lines of bom_costBill, one per occurrence.</p>}
      </section>
      {master.data && (
        <section>
          <SectionTitle>Item master</SectionTitle>
          <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-[12.5px]">
            <KV k="Category" v={master.data.category ?? '–'} />
            <KV k="Stock unit" v={master.data.stockUomCode} />
            <KV k="Make / buy" v={master.data.makeBuy} />
            <KV k="Shelf life" v={master.data.shelfLifeDays ? `${master.data.shelfLifeDays} days` : '–'} />
          </div>
        </section>
      )}
    </div>
  )
}

function PartDetail({
  model,
  part,
  currency,
  onClose,
  onMaterial,
  onPart,
}: {
  model: StudioModel
  part: PartKey
  currency: string
  onClose: () => void
  onMaterial: (i: string) => void
  onPart: (p: PartKey) => void
}) {
  const navigate = useNavigate()
  const p = model.parts.get(part)
  const feeding = model.materials.filter((m) => m.feeds.includes(part) && !m.byproduct)
  const inside = subPartsOf(model, part)
  if (!p && part !== 'core') return null
  const line = p?.node.line ?? model.root.line
  const cost = p?.node.cost ?? model.root.cost
  return (
    <div className="flex flex-col gap-5">
      <Header title={p?.name ?? model.root.line.description} code={line.item} onClose={onClose} tone="violet" kind="Part of the product" color={PART_COLOR[part]} />
      <p className="-mt-2 text-[13px] text-ink-300">{line.description}</p>
      <div className="grid grid-cols-2 gap-2.5">
        <div className="rounded-2xl bg-white/[0.04] p-3.5 ring-1 ring-white/[0.08] ring-inset">
          <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Required</div>
          <div className="mt-1 text-xl font-semibold text-ink-50 tabular">
            {fmtQty(line.required)} <span className="text-[11px] text-ink-400">{line.unit}</span>
          </div>
        </div>
        <div className="rounded-2xl bg-white/[0.04] p-3.5 ring-1 ring-white/[0.08] ring-inset">
          <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Line cost</div>
          <div className="mt-1 text-xl font-semibold text-ink-50 tabular" title={exact(cost?.extendedCost)}>
            {cost ? fmtMoney(cost.extendedCost, currency) : '–'}
          </div>
          {cost && (
            <div className="text-[10.5px] text-ink-500">
              {cost.element} · {cost.source}
            </div>
          )}
        </div>
      </div>
      {inside.length > 0 && (
        <section>
          <SectionTitle>Assembled from</SectionTitle>
          <div className="flex flex-wrap gap-2">
            {inside.map((q) => (
              <button
                key={q}
                onClick={() => onPart(q)}
                className="flex cursor-pointer items-center gap-2 rounded-xl bg-white/[0.05] px-3 py-1.5 text-[12.5px] text-ink-100 ring-1 ring-white/10 ring-inset hover:bg-white/[0.09]"
              >
                <span className="size-2 rounded-full" style={{ background: PART_COLOR[q] }} />
                {model.parts.get(q)?.name ?? q}
              </button>
            ))}
          </div>
        </section>
      )}
      <section>
        <SectionTitle>Made from</SectionTitle>
        <div className="flex flex-col gap-1.5">
          {feeding.map((m) => (
            <button
              key={m.item}
              onClick={() => onMaterial(m.item)}
              className="flex cursor-pointer items-center gap-2 rounded-xl bg-white/[0.03] px-3 py-2 text-left ring-1 ring-white/[0.06] ring-inset hover:bg-white/[0.07]"
            >
              <span className="size-2 rounded-full" style={{ background: m.tint, boxShadow: `0 0 6px ${m.tint}` }} />
              <span className="truncate text-[12.5px] text-ink-100">{m.name}</span>
              <span className="ml-auto text-[11.5px] text-ink-400 tabular">{materialFigure(m)}</span>
            </button>
          ))}
          {feeding.length === 0 && <p className="text-[12.5px] text-ink-400">Bought in as it is.</p>}
        </div>
      </section>
      <Button size="sm" icon={<Network className="size-3.5" />} onClick={() => navigate(`/explode/${encodeURIComponent(line.item)}`)}>
        Explode {line.item}
      </Button>
    </div>
  )
}

function Header({ title, code, onClose, tone, kind, color }: { title: string; code: string; onClose: () => void; tone: string; kind: string; color?: string }) {
  return (
    <div className="flex items-start gap-3">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          {color && <span className="size-2.5 rounded-full" style={{ background: color, boxShadow: `0 0 10px ${color}` }} />}
          <Badge tone={tone}>{kind}</Badge>
        </div>
        <h2 className="mt-2 font-display text-[22px] leading-tight font-semibold text-ink-50">{title}</h2>
        <Code className="text-[12px] text-ink-300">{code}</Code>
      </div>
      <IconButton label="Close" onClick={onClose}>
        <X className="size-4" />
      </IconButton>
    </div>
  )
}

function SectionTitle({ children }: { children: ReactNode }) {
  return (
    <div className="mb-2 flex items-center gap-2">
      <h3 className="text-[11px] font-semibold tracking-[0.12em] text-ink-200 uppercase">{children}</h3>
      <span className="h-px flex-1 bg-white/[0.06]" />
    </div>
  )
}

function KV({ k, v }: { k: string; v: string }) {
  return (
    <div>
      <div className="text-[10.5px] tracking-wide text-ink-500 uppercase">{k}</div>
      <div className="text-ink-100">{v}</div>
    </div>
  )
}

function ToolToggle({ on, onClick, icon, label }: { on: boolean; onClick: () => void; icon: ReactNode; label: string }) {
  return (
    <button
      onClick={onClick}
      className={cn(
        'flex h-9 cursor-pointer items-center gap-2 rounded-xl px-3 text-[12.5px] font-medium transition',
        on ? 'bg-white/[0.1] text-white ring-1 ring-white/15 ring-inset' : 'text-ink-300 hover:bg-white/[0.05] hover:text-ink-100',
      )}
    >
      {icon}
      {label}
    </button>
  )
}

function StudioLoading() {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4 bg-[radial-gradient(ellipse_at_center,rgba(58,36,64,0.6),transparent_70%)]">
      <div className="relative size-16">
        <span className="absolute inset-0 animate-pulse-ring rounded-full bg-pink-300/30" />
        <span className="absolute inset-4 rounded-full bg-[linear-gradient(135deg,#fde68a,#f9a8d4,#c4b5fd)] shadow-[0_0_40px_rgba(249,168,212,0.8)]" />
      </div>
      <p className="text-sm text-ink-300">Setting up the studio from the explosion…</p>
    </div>
  )
}

function StudioStart({ onPick }: { onPick: (code: string) => void }) {
  return (
    <div className="flex h-full items-center justify-center p-8">
      <div className="glass flex w-full max-w-2xl flex-col items-center rounded-3xl p-10">
        <EmptyState icon={<Cake className="size-6" />} title="The product studio">
          Pick a product: GUIv2 explodes it through the STERP API and stages everything that goes into it. Bakery products are rendered as the cake itself,
          layer by layer, and bicycles in the bike atelier, every part laid out and built into the bike.
        </EmptyState>
        <div className="mt-2 flex w-full max-w-md flex-col gap-3">
          <ItemPicker value="" onChange={(v) => v && onPick(v)} placeholder="Item code, e.g. FG-CAKE-CELEB-8IN" autoFocus />
          <Button variant="warm" icon={<Cake className="size-4" />} onClick={() => onPick('FG-CAKE-CELEB-8IN')}>
            Open the celebration cake
          </Button>
          <Button variant="secondary" icon={<Bike className="size-4" />} onClick={() => onPick('FG-BIKE-COMMUTE-M')}>
            Open the commuter bicycle
          </Button>
        </div>
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- helpers

/** The API figure of a material: the engine's leaf requirement, or the by-product line's recovery. */
function materialFigure(m: StudioMaterial): string {
  if (m.leaf) return `${fmtQty(m.leaf.required, true)} ${m.leaf.unit}`
  const o = m.occurrences[0]
  return o ? `${fmtQty(o.line.required, true)} ${o.line.unit}` : ''
}

function shortTitle(description: string) {
  const first = description.split(',')[0]
  return first.length > 4 ? first : description
}
