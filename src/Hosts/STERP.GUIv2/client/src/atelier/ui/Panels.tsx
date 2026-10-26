import { ArrowRight, Check, Network, X } from 'lucide-react'
import { useEffect, useMemo, useRef, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import type { ExplosionLine } from '@/api/types'
import { useBillComponents, useBillRevisions, useItem } from '@/api/hooks'
import { CLASS_META, classify } from '@/domain/kinds'
import { exact, fmtMoney, fmtQty, fmtRate } from '@/domain/format'
import { Badge, Button, Code, IconButton, cn } from '@/components/ui/primitives'
import { stepIndexAt } from '../evaluate'
import { useAtelierTimeline } from '../store'
import type { ANode, AtelierModel } from '../types'

// The panels beside the atelier: the build sheet (the explosion as a tree, each line's state in the build) and the detail of
// a selected line. Every figure is the API's: usage and requirement from bom_explodeBill, costs from bom_costBill.

const TONE: Record<string, string> = { made: 'violet', bought: 'cyan', service: 'emerald', phantom: 'slate', byproduct: 'amber', note: 'slate', root: 'pink' }

function walk(n: ANode, out: ANode[] = []): ANode[] {
  out.push(n)
  for (const c of n.children) walk(c, out)
  return out
}

function stateOf(n: ANode, p: number): 'todo' | 'now' | 'done' {
  const w = n.role === 'make' || n.role === 'root' ? n.build : n.action
  if (!w) return p >= 1 ? 'done' : 'todo'
  if (p >= w[1]) return 'done'
  return p > w[0] ? 'now' : 'todo'
}

export function BuildSheet({ model, focus, onSelect, onHover, asOf }: { model: AtelierModel; focus: string | null; onSelect: (id: string) => void; onHover: (id: string | null) => void; asOf: string }) {
  const progress = useAtelierTimeline((s) => s.progress)
  const playing = useAtelierTimeline((s) => s.playing)
  const rows = useMemo(() => walk(model.root), [model])
  const stepNode = model.steps[Math.min(model.steps.length - 1, stepIndexAt(model, progress))]?.node.id
  const activeRow = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (playing) activeRow.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }, [stepNode, playing])
  const made = model.nodes.filter((n) => n.role === 'make').length
  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className="text-[11px] font-semibold tracking-[0.14em] text-cyan-200/90 uppercase">Build sheet</div>
        <p className="mt-1 text-[13px] leading-relaxed text-ink-300">{model.root.line.description}</p>
        <p className="mt-1 text-[11.5px] text-ink-500">
          {model.nodes.length} lines in the explosion · {made} subassemblies · {model.pieces.length} parts drawn
        </p>
      </div>
      <div className="-mx-2 flex flex-col">
        {rows.map((n) => {
          const st = stateOf(n, progress)
          const cls = classify(n.line)
          const isStep = n.id === stepNode && st !== 'done'
          return (
            <button
              key={n.id}
              ref={isStep ? activeRow : undefined}
              onClick={() => onSelect(n.id)}
              onPointerEnter={() => onHover(n.id)}
              onPointerLeave={() => onHover(null)}
              className={cn(
                'group flex cursor-pointer items-center gap-2 rounded-lg py-[5px] pr-2 text-left transition-colors',
                focus === n.id ? 'bg-white/[0.09]' : isStep ? 'bg-cyan-300/[0.07]' : 'hover:bg-white/[0.05]',
              )}
              style={{ paddingLeft: 8 + n.depth * 13 }}
            >
              <span
                className={cn('flex size-3.5 shrink-0 items-center justify-center rounded-full ring-1 ring-inset', st === 'now' && 'animate-pulse')}
                style={{ background: st === 'done' ? n.color : st === 'now' ? `${n.color}55` : 'transparent', boxShadow: st !== 'todo' ? `0 0 8px ${n.color}88` : undefined, ['--tw-ring-color' as string]: n.color }}
              >
                {st === 'done' && <Check className="size-2.5 text-ink-950" strokeWidth={3.5} />}
              </span>
              <span className={cn('min-w-0 flex-1 truncate text-[12.5px]', n.role === 'make' || n.role === 'root' ? 'font-semibold text-ink-50' : st === 'done' ? 'text-ink-300' : 'text-ink-200')}>
                {n.name}
              </span>
              {cls !== 'bought' && cls !== 'root' && <span className="shrink-0 text-[9.5px] font-semibold tracking-wider uppercase" style={{ color: CLASS_META[cls].color }}>{CLASS_META[cls].label}</span>}
              <span className="shrink-0 text-[11px] text-ink-500 tabular">{n.role === 'root' ? '' : `${fmtQty(n.line.usage, true)} ${n.line.unit.trim()}`}</span>
            </button>
          )
        })}
      </div>
      <WorkInstruction root={model.root.line} asOf={asOf} />
    </div>
  )
}

function WorkInstruction({ root, asOf }: { root: ExplosionLine; asOf: string }) {
  const bills = useBillRevisions({ parentItemCode: root.item, asOf })
  const bill = bills.data?.find((b) => b.isEffectiveNow) ?? bills.data?.[0]
  const components = useBillComponents(bill?.bomRef, bill?.bomRevision, asOf)
  const notes = (components.data ?? []).filter((c) => c.lineKind.trim() === 'NOTE' && c.notes)
  if (!notes.length) return null
  return (
    <section className="rounded-2xl bg-[linear-gradient(135deg,rgba(253,230,138,0.08),rgba(103,232,249,0.06))] p-4 ring-1 ring-amber-200/15 ring-inset">
      <div className="mb-1.5 text-[11px] font-semibold tracking-[0.12em] text-amber-100 uppercase">Work instruction</div>
      {notes.map((c) => (
        <p key={c.lineNum} className="font-display text-[15px] leading-snug text-ink-50 italic">
          “{c.notes}”
        </p>
      ))}
      <p className="mt-2 font-mono text-[10.5px] text-ink-400">
        {bill?.bomRef} line {notes[0].lineNum} · bom_listBillComponents
      </p>
    </section>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section>
      <div className="mb-2 flex items-center gap-2">
        <h3 className="text-[11px] font-semibold tracking-[0.12em] text-ink-200 uppercase">{title}</h3>
        <span className="h-px flex-1 bg-white/[0.06]" />
      </div>
      {children}
    </section>
  )
}

function Chip({ n, onSelect }: { n: ANode; onSelect: (id: string) => void }) {
  return (
    <button
      onClick={() => onSelect(n.id)}
      className="flex max-w-full cursor-pointer items-center gap-2 rounded-xl bg-white/[0.05] px-2.5 py-1.5 text-left text-[12px] text-ink-100 ring-1 ring-white/10 ring-inset hover:bg-white/[0.09]"
    >
      <span className="size-2 shrink-0 rounded-full" style={{ background: n.color }} />
      <span className="truncate">{n.name}</span>
      <span className="shrink-0 text-[10.5px] text-ink-500 tabular">
        {fmtQty(n.line.usage, true)} {n.line.unit.trim()}
      </span>
    </button>
  )
}

export function NodeDetail({
  model,
  id,
  leaf,
  currency,
  onSelect,
  onClose,
}: {
  model: AtelierModel
  id: string
  leaf: ExplosionLine[]
  currency: string
  onSelect: (id: string) => void
  onClose: () => void
}) {
  const navigate = useNavigate()
  const n = model.byId.get(id)
  const master = useItem(n?.line.item)
  if (!n) return null
  const cls = classify(n.line)
  const meta = CLASS_META[cls]
  const cost = n.tree.cost
  const ancestors: ANode[] = []
  for (let a = n.parent; a; a = a.parent) ancestors.unshift(a)
  const others = model.nodes.filter((x) => x.line.item === n.line.item && x.id !== n.id)
  const engineLeaf = leaf.find((l) => l.item === n.line.item)
  const step = model.steps.find((s) => s.node.id === n.id) ?? model.steps.find((s) => s.node.id === n.owner?.id)
  const parentUnit = n.parent ? (n.parent.role === 'root' ? 'bike' : n.parent.name.toLowerCase()) : ''
  return (
    <div className="flex flex-col gap-5">
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="size-2.5 rounded-full" style={{ background: n.color, boxShadow: `0 0 10px ${n.color}` }} />
            <Badge tone={TONE[cls] ?? 'slate'}>{meta.label}</Badge>
            {n.find && <span className="text-[11px] text-ink-500">find {n.find}</span>}
          </div>
          <h2 className="mt-2 font-display text-[22px] leading-tight font-semibold text-ink-50">{n.name}</h2>
          <Code className="text-[12px] text-ink-300">{n.line.item}</Code>
        </div>
        <IconButton label="Close" onClick={onClose}>
          <X className="size-4" />
        </IconButton>
      </div>
      <p className="-mt-2 text-[13px] text-ink-300">{n.line.description}</p>

      {n.role !== 'root' && (
        <div className="grid grid-cols-2 gap-2.5">
          <div className="rounded-2xl bg-white/[0.04] p-3.5 ring-1 ring-white/[0.08] ring-inset">
            <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Per {parentUnit}</div>
            <div className="mt-1 text-xl font-semibold text-ink-50 tabular" title={exact(n.line.usage)}>
              {fmtQty(n.line.usage, true)} <span className="text-[11px] text-ink-400">{n.line.unit.trim()}</span>
            </div>
            <div className="text-[10.5px] text-ink-500">usage on the bill · {n.line.basis.trim().toLowerCase()}</div>
          </div>
          <div className="rounded-2xl bg-white/[0.04] p-3.5 ring-1 ring-white/[0.08] ring-inset">
            <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">This batch</div>
            <div className="mt-1 text-xl font-semibold text-ink-50 tabular" title={exact(n.line.required)}>
              {fmtQty(n.line.required, true)} <span className="text-[11px] text-ink-400">{n.line.unit.trim()}</span>
            </div>
            <div className="text-[10.5px] text-ink-500">required · bom_explodeBill</div>
          </div>
        </div>
      )}

      {cost && (
        <div className="flex items-baseline justify-between rounded-2xl bg-white/[0.04] px-4 py-3 ring-1 ring-white/[0.08] ring-inset">
          <div>
            <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Line cost</div>
            <div className="text-[11px] text-ink-500">
              {fmtRate(cost.unitCost, currency)} / {cost.unit} · {cost.element} · {cost.source}
            </div>
          </div>
          <div className="font-display text-[22px] font-semibold text-ink-50 tabular" title={exact(cost.extendedCost)}>
            {fmtMoney(cost.extendedCost, currency)}
          </div>
        </div>
      )}

      {ancestors.length > 0 && (
        <Section title="Goes into">
          <div className="flex flex-wrap items-center gap-1.5">
            {ancestors.map((a, i) => (
              <span key={a.id} className="flex items-center gap-1.5">
                {i > 0 && <ArrowRight className="size-3 text-ink-500" />}
                <button onClick={() => onSelect(a.id)} className="cursor-pointer rounded-lg px-2 py-1 text-[12px] text-ink-200 ring-1 ring-white/10 ring-inset hover:bg-white/[0.07]">
                  {a.name}
                </button>
              </span>
            ))}
          </div>
        </Section>
      )}

      {n.children.length > 0 && (
        <Section title={n.role === 'phantom' ? 'Kit contents' : 'Made from'}>
          <div className="flex flex-col gap-1.5">
            {n.children.map((c) => (
              <Chip key={c.id} n={c} onSelect={onSelect} />
            ))}
          </div>
        </Section>
      )}

      {(others.length > 0 || engineLeaf) && (
        <Section title="Across the explosion">
          {engineLeaf && (
            <p className="mb-2 text-[12px] text-ink-300">
              Engine leaf total (LEAF):{' '}
              <span className="font-semibold text-ink-50 tabular" title={exact(engineLeaf.required)}>
                {fmtQty(engineLeaf.required, true)} {engineLeaf.unit.trim()}
              </span>
            </p>
          )}
          <div className="flex flex-col gap-1.5">
            {others.map((o) => (
              <button
                key={o.id}
                onClick={() => onSelect(o.id)}
                className="flex cursor-pointer items-center justify-between gap-2 rounded-xl bg-white/[0.03] px-3 py-2 text-left text-[12px] ring-1 ring-white/[0.06] ring-inset hover:bg-white/[0.07]"
              >
                <span className="truncate text-ink-300">
                  in <span className="text-ink-100">{o.parent?.name}</span>
                </span>
                <span className="shrink-0 text-ink-400 tabular">
                  {fmtQty(o.line.required, true)} {o.line.unit.trim()}
                </span>
              </button>
            ))}
          </div>
        </Section>
      )}

      {master.data && (
        <Section title="Item master">
          <div className="grid grid-cols-2 gap-x-4 gap-y-2 text-[12.5px]">
            {[
              ['Category', master.data.category ?? '–'],
              ['Stock unit', master.data.stockUomCode],
              ['Make / buy', master.data.makeBuy],
              ['Shelf life', master.data.shelfLifeDays ? `${master.data.shelfLifeDays} days` : '–'],
            ].map(([k, v]) => (
              <div key={k}>
                <div className="text-[10.5px] tracking-wide text-ink-500 uppercase">{k}</div>
                <div className="text-ink-100">{v}</div>
              </div>
            ))}
          </div>
        </Section>
      )}

      <div className="flex flex-wrap gap-2">
        {step && (
          <Button size="sm" icon={<ArrowRight className="size-3.5" />} onClick={() => useAtelierTimeline.getState().scrub(Math.min(1, step.end + 0.002))}>
            Show it built
          </Button>
        )}
        <Button size="sm" icon={<Network className="size-3.5" />} onClick={() => navigate(`/explode/${encodeURIComponent(n.line.item)}`)}>
          Explode {n.line.item}
        </Button>
      </div>
    </div>
  )
}

/** The card under the pointer. */
export function PieceTip({ n }: { n: ANode }) {
  const cls = classify(n.line)
  return (
    <div className="max-w-[300px] rounded-xl border border-white/12 bg-ink-900/90 px-3 py-2 shadow-[0_12px_32px_-12px_rgba(0,0,0,0.9)] backdrop-blur-md">
      <div className="flex items-center gap-2">
        <span className="size-2 rounded-full" style={{ background: n.color, boxShadow: `0 0 8px ${n.color}` }} />
        <span className="truncate text-[12.5px] font-semibold text-ink-50">{n.name}</span>
        <span className="ml-auto shrink-0 text-[10px] font-semibold tracking-wider uppercase" style={{ color: CLASS_META[cls].color }}>
          {CLASS_META[cls].label}
        </span>
      </div>
      <div className="mt-0.5 font-mono text-[11px] text-ink-300">{n.line.item}</div>
      {n.role !== 'root' && (
        <div className="mt-1 text-[11.5px] text-ink-300 tabular">
          {fmtQty(n.line.usage, true)} {n.line.unit.trim()} per parent · {fmtQty(n.line.required, true)} {n.line.unit.trim()} this batch
        </div>
      )}
    </div>
  )
}
