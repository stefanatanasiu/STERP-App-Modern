import { AnimatePresence, motion } from 'motion/react'
import { ArrowLeft, Bike, Crosshair, Info, Layers, Moon, Package, ScanLine, Sun, Video, VideoOff, Wrench } from 'lucide-react'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import type { CostedExplosion, Envelope, ExplosionLine } from '@/api/types'
import type { Tree } from '@/domain/tree'
import { exact, fmtMoney, fmtQty } from '@/domain/format'
import { Badge, Button, Code, IconButton, cn } from '@/components/ui/primitives'
import { NumberInput, Segmented } from '@/components/ui/forms'
import { Atelier } from './Atelier'
import type { View } from './evaluate'
import { buildAtelier } from './model'
import { useAtelierTimeline } from './store'
import type { ANode } from './types'
import { BuildTimeline } from './ui/BuildTimeline'
import { BuildSheet, NodeDetail, PieceTip } from './ui/Panels'

// The bicycle's studio: the bike atelier. Every line of the live explosion is drawn as the part it is, laid out flat in its
// subassembly's cell; the build timeline assembles them bottom up into the bike. GUIv2 only reads the APIs: the explosion
// (INDENTED and LEAF), the costed explosion, the item master and the bill's components.

const BATCHES = [1, 6, 12, 24]

export interface BikeStudioProps {
  item: string
  tree: Tree
  leaf: ExplosionLine[]
  costed: { data?: Envelope<CostedExplosion>; error: unknown }
  category: string | null | undefined
  quantity: number
  onQuantity: (q: number) => void
  asOf: string
  currency: string
  onBack: () => void
  banner?: ReactNode
}

export default function BikeStudio({ item, tree, leaf, costed, category, quantity, onQuantity, asOf, currency, onBack, banner }: BikeStudioProps) {
  const model = useMemo(() => buildAtelier(tree), [tree])
  const [view, setView] = useState<View>('build')
  const [blueprint, setBlueprint] = useState(false)
  const [lit, setLit] = useState(false)
  const [packaging, setPackaging] = useState(false)
  const [follow, setFollow] = useState(true)
  const [selected, setSelected] = useState<string | null>(null)
  const [hovered, setHovered] = useState<string | null>(null)
  const [fitToken, setFitToken] = useState(0)

  // the bike builds itself from the flat lay once when it opens
  useEffect(() => {
    useAtelierTimeline.getState().restart()
  }, [item])
  useEffect(() => {
    if (view !== 'build') useAtelierTimeline.getState().pause()
  }, [view])

  const caption = useMemo(
    () => (n: ANode) => {
      const per = n.parent?.role === 'root' ? 'bike' : (n.owner?.name.split(' ').slice(0, 2).join(' ').toLowerCase() ?? 'set')
      const unit = n.line.unit.trim()
      return {
        title: n.name.length > 26 ? `${n.name.slice(0, 25)}…` : n.name,
        figure: n.role === 'note' ? 'instruction' : `${fmtQty(n.line.usage, true)} ${unit} per ${per} · ${fmtQty(n.line.required, true)} ${unit}`,
      }
    },
    [],
  )

  const focus = hovered ?? selected
  const insets = { left: 28, right: 404, top: 178, bottom: view === 'build' ? 186 : 96 }

  return (
    <div className="relative h-full min-h-[680px] overflow-hidden bg-[#05080e]">
      <Atelier
        model={model}
        view={view}
        blueprint={blueprint}
        packaging={packaging}
        lit={lit}
        follow={follow}
        focus={focus}
        onHover={setHovered}
        onSelect={(id) => setSelected(id && id === selected ? null : id)}
        caption={caption}
        tooltip={(n) => <PieceTip n={n} />}
        fitToken={fitToken}
        insets={insets}
      />

      {/* title and view */}
      <div className="pointer-events-none absolute inset-x-0 top-0 z-20 flex items-start gap-4 p-5">
        <div className="glass-strong pointer-events-auto flex items-center gap-4 rounded-3xl py-3.5 pr-5 pl-4">
          <IconButton label="Back to the explosion" onClick={onBack}>
            <ArrowLeft className="size-4" />
          </IconButton>
          <div className="flex size-11 items-center justify-center rounded-2xl bg-[linear-gradient(135deg,#a5f3fc,#67e8f9_45%,#a78bfa)] shadow-[0_8px_26px_-8px_rgba(103,232,249,0.8)]">
            <Bike className="size-5 text-ink-950" />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h1 className="font-display text-[22px] leading-tight font-semibold tracking-tight text-ink-50 italic">{model.root.line.description.split(',')[0]}</h1>
              {category && <Badge tone="cyan">{category}</Badge>}
            </div>
            <div className="mt-0.5 flex items-center gap-2 text-[12px] text-ink-300">
              <Code className="text-[12px] text-ink-200">{item}</Code>
              <span className="text-ink-500">·</span>
              <span>Bike atelier · every line of the live STERP explosion</span>
            </div>
          </div>
        </div>
        <div className="flex-1" />
        <Segmented<View>
          className="glass-strong pointer-events-auto"
          value={view}
          onChange={(v) => {
            setView(v)
            if (v === 'build') useAtelierTimeline.getState().play()
          }}
          options={[
            { value: 'build', label: 'Build', icon: <Wrench className="size-3.5" />, title: 'From the flat lay to the bike, step by step' },
            { value: 'assembled', label: 'Assembled', icon: <Bike className="size-3.5" />, title: 'The finished bike' },
            { value: 'exploded', label: 'Exploded', icon: <Layers className="size-3.5" />, title: 'Exploded drawing with find numbers' },
          ]}
        />
      </div>

      {/* batch and cost */}
      <div className="absolute top-[104px] left-5 z-20">
        <BatchBar quantity={quantity} onQuantity={onQuantity} costed={costed} currency={currency} />
      </div>

      {/* the build sheet or the selected line */}
      <div className="absolute top-[108px] right-5 bottom-5 z-20 w-[370px]">
        <div className="glass-strong h-full overflow-y-auto rounded-3xl p-5">
          <AnimatePresence mode="wait">
            <motion.div key={selected ?? 'sheet'} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -6 }} transition={{ duration: 0.2 }}>
              {selected ? (
                <NodeDetail model={model} id={selected} leaf={leaf} currency={currency} onSelect={setSelected} onClose={() => setSelected(null)} />
              ) : (
                <BuildSheet model={model} focus={focus} onSelect={setSelected} onHover={setHovered} asOf={asOf} />
              )}
            </motion.div>
          </AnimatePresence>
        </div>
      </div>

      {/* stage controls */}
      <div className="glass-strong absolute bottom-5 left-5 z-20 flex items-center gap-1 rounded-2xl p-1.5">
        <ToolToggle on={blueprint} onClick={() => setBlueprint((v) => !v)} icon={<ScanLine className="size-4" />} label="Blueprint" />
        <ToolToggle on={lit} onClick={() => setLit((v) => !v)} icon={lit ? <Moon className="size-4" /> : <Sun className="size-4" />} label={lit ? 'Night ride' : 'Daylight'} />
        <ToolToggle on={packaging} onClick={() => setPackaging((v) => !v)} icon={<Package className="size-4" />} label="Packaging" />
        {view === 'build' && <ToolToggle on={follow} onClick={() => setFollow((v) => !v)} icon={follow ? <Video className="size-4" /> : <VideoOff className="size-4" />} label="Follow" />}
        <span className="mx-1 h-6 w-px bg-white/10" />
        <Button
          size="sm"
          variant="ghost"
          icon={<Crosshair className="size-3.5" />}
          onClick={() => {
            setSelected(null)
            setFitToken((t) => t + 1)
          }}
        >
          Fit
        </Button>
      </div>
      {view === 'build' ? (
        <div className="absolute right-[410px] bottom-[78px] left-5 z-20 mx-auto max-w-[900px]">
          <BuildTimeline model={model} />
        </div>
      ) : (
        !selected && (
          <div className="pointer-events-none absolute bottom-[78px] left-5 z-20 flex items-center gap-2 rounded-xl bg-ink-950/50 px-3 py-1.5 text-[12px] text-ink-200 backdrop-blur">
            <Info className="size-3.5 text-cyan-300" />
            {view === 'exploded' ? 'Balloons carry the find numbers of the bill. Click a part for its line.' : 'Hover any part for its bill line; click it for usage, requirement and cost.'}
          </div>
        )
      )}
      {banner && <div className="absolute bottom-[78px] left-1/2 z-20 -translate-x-1/2">{banner}</div>}
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

/** Batch size and the costed explosion's figures in one bar: the stage keeps the room for the mat. */
function BatchBar({ quantity, onQuantity, costed, currency }: { quantity: number; onQuantity: (q: number) => void; costed: BikeStudioProps['costed']; currency: string }) {
  const c = costed.data?.data
  const [custom, setCustom] = useState('')
  const breakdown = c ? `Material ${fmtMoney(c.materialCost, currency)} · conversion ${fmtMoney(c.conversionCost, currency)} · recovery ${fmtMoney(c.recoveryCredit, currency)}` : ''
  return (
    <div className="glass-strong flex items-center gap-4 rounded-2xl py-2 pr-4 pl-3">
      <div className="flex items-center gap-1.5">
        <span className="mr-1 text-[10.5px] font-semibold tracking-[0.12em] text-ink-300 uppercase">Batch</span>
        {BATCHES.map((b) => (
          <button
            key={b}
            onClick={() => onQuantity(b)}
            className={cn(
              'h-7 min-w-8 cursor-pointer rounded-lg px-2 text-[12.5px] font-semibold ring-1 transition tabular ring-inset',
              quantity === b ? 'bg-[linear-gradient(135deg,rgba(103,232,249,0.32),rgba(167,139,250,0.3))] text-white ring-cyan-200/40' : 'bg-white/[0.04] text-ink-200 ring-white/10 hover:bg-white/[0.08]',
            )}
          >
            {b}
          </button>
        ))}
        <div className="w-14">
          <NumberInput
            value={custom}
            placeholder="…"
            min={1}
            className="h-7 px-2 text-[12.5px]"
            onChange={(e) => setCustom(e.target.value)}
            onKeyDown={(e) => {
              const v = Number(custom)
              if (e.key === 'Enter' && v > 0) onQuantity(v)
            }}
          />
        </div>
      </div>
      <span className="h-8 w-px bg-white/10" />
      <div title={`bom_costBill · ${breakdown}`}>
        <div className="text-[9.5px] tracking-wide text-ink-400 uppercase">Cost per bike</div>
        <div className="font-display text-[19px] leading-none font-semibold text-gradient tabular" title={exact(c?.unitCost)}>
          {c ? fmtMoney(c.unitCost, currency) : costed.error ? '–' : '…'}
        </div>
      </div>
      <div>
        <div className="text-[9.5px] tracking-wide text-ink-400 uppercase">Batch of {fmtQty(quantity)}</div>
        <div className="text-[15px] leading-tight font-semibold text-ink-50 tabular" title={exact(c?.totalCost)}>
          {c ? fmtMoney(c.totalCost, currency) : costed.error ? '–' : '…'}
        </div>
      </div>
      {c && (
        <div className="hidden flex-col text-[10.5px] leading-tight text-ink-400 tabular xl:flex">
          <span>Material {fmtMoney(c.materialCost, currency)}</span>
          <span>
            Conversion {fmtMoney(c.conversionCost, currency)} · recovery {fmtMoney(c.recoveryCredit, currency)}
          </span>
        </div>
      )}
      {costed.error ? <span className="text-[11.5px] text-amber-200">Costing not available</span> : null}
    </div>
  )
}

