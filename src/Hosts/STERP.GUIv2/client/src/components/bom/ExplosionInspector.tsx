import { motion } from 'motion/react'
import { ArrowUpRight, Boxes, Cake, ChevronRight, CircleAlert, GitFork, Library, Network, Sparkles } from 'lucide-react'
import type { ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { useBillRevisions, useItem } from '@/api/hooks'
import type { ExplosionLine } from '@/api/types'
import { ancestors, type Tree, type TreeNode } from '@/domain/tree'
import { CLASS_META, classify, STOP_REASON_TEXT } from '@/domain/kinds'
import { exact, fmtMoney, fmtQty, fmtRate } from '@/domain/format'
import { Badge, Button, Code, Skeleton, StatusChip } from '@/components/ui/primitives'
import { KeyValues } from '@/components/ui/surfaces'

export function OccurrenceInspector({
  node,
  asOf,
  currency,
  onSelect,
}: {
  node: TreeNode
  asOf: string
  currency: string
  onSelect: (id: string) => void
}) {
  const navigate = useNavigate()
  const line = node.line
  const cls = classify(line)
  const meta = CLASS_META[cls]
  const item = useItem(line.item)
  const made = line.kind.trim() === 'INTERMED' || line.kind.trim() === 'PHANTOM'
  const bills = useBillRevisions(made ? { parentItemCode: line.item, asOf } : null)
  const chain = ancestors(node).reverse()
  const stop = line.stopReason.trim()

  return (
    <motion.div key={node.id} initial={{ opacity: 0, x: 12 }} animate={{ opacity: 1, x: 0 }} transition={{ duration: 0.25 }} className="flex flex-col gap-5">
      <div>
        <div className="mb-3 flex flex-wrap items-center gap-1 text-[11px] text-ink-400">
          {chain.map((a) => (
            <span key={a.id} className="flex items-center gap-1">
              <button onClick={() => onSelect(a.id)} className="cursor-pointer font-mono hover:text-ink-100">
                {a.line.item}
              </button>
              <ChevronRight className="size-3" />
            </span>
          ))}
        </div>
        <div className="flex items-start gap-3">
          <div className="relative mt-1 size-10 shrink-0 rounded-2xl ring-1 ring-white/15" style={{ background: `radial-gradient(circle at 30% 30%, #fff8, ${meta.color} 45%, #0000 75%)` }}>
            <span className="absolute inset-0 rounded-2xl blur-lg" style={{ background: meta.color, opacity: 0.35 }} />
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone={toneOf(cls)}>{meta.label}</Badge>
              <span className="text-[11px] text-ink-400">level {line.level}</span>
              {node.inferred && (
                <span title="The API cut this occurrence path at 80 characters; its position follows the engine order." className="text-[11px] text-amber-300">
                  position inferred
                </span>
              )}
            </div>
            <h2 className="mt-1.5 font-mono text-[17px] font-semibold tracking-tight break-all text-ink-50">{line.item}</h2>
            <p className="mt-0.5 text-[13px] leading-snug text-ink-300">{line.description}</p>
          </div>
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2.5">
        <Figure label="Required" value={`${fmtQty(line.required)}`} unit={line.unit} title={exact(line.required)} accent={meta.color} />
        <Figure label={line.basis === 'FIXEDRUN' ? 'Usage per run' : 'Usage per parent'} value={fmtQty(line.usage)} unit={line.basis} title={exact(line.usage)} />
      </div>

      {line.warning.trim() && (
        <div className="flex items-start gap-2.5 rounded-xl bg-amber-400/[0.08] px-3 py-2.5 text-[12.5px] text-amber-100 ring-1 ring-amber-300/20 ring-inset">
          <CircleAlert className="mt-0.5 size-4 shrink-0 text-amber-300" />
          {line.warning}
        </div>
      )}

      <Section title="Explosion line" hint="bom_explodeBill">
        <KeyValues
          items={[
            ['Kind', <Code key="k">{line.kind}</Code>],
            ['Supply', <Code key="s">{line.supply}</Code>],
            ['Unrounded', <span key="u" title={exact(line.unrounded)}>{fmtQty(line.unrounded)}</span>],
            ['Revision', line.revision.trim() || '–'],
            ['Stop reason', <span key="r" title={STOP_REASON_TEXT[stop] ?? stop}>{stop}</span>],
            ['Basis', line.basis],
          ]}
        />
        {STOP_REASON_TEXT[stop] && <p className="mt-2.5 text-[12px] text-ink-400">{STOP_REASON_TEXT[stop]}.</p>}
      </Section>

      {node.cost && (
        <Section title="Cost" hint="bom_costBill">
          <div className="mb-3 flex items-end justify-between gap-3">
            <div>
              <div className="text-[11px] tracking-wide text-ink-400 uppercase">Extended cost</div>
              <div className="text-2xl font-semibold tracking-tight text-ink-50 tabular" title={exact(node.cost.extendedCost)}>
                {fmtMoney(node.cost.extendedCost, currency)}
              </div>
            </div>
            <Badge tone="violet">{node.cost.element || 'no element'}</Badge>
          </div>
          <KeyValues
            items={[
              ['Unit cost', <span key="c" title={exact(node.cost.unitCost)}>{fmtRate(node.cost.unitCost, currency)}</span>],
              ['Rate source', <Code key="s">{node.cost.source}</Code>],
              ['Costed quantity', `${fmtQty(node.cost.required)} ${node.cost.unit}`],
              ['Line warning', node.cost.warning.trim() || '–'],
            ]}
          />
        </Section>
      )}

      {node.net && (
        <Section title="Netting" hint="bom_netBillRequirement">
          <KeyValues
            items={[
              ['Required', `${fmtQty(node.net.required)} ${node.net.unit}`],
              ['Stock used', fmtQty(node.net.stockUsed)],
              ['Inbound used', fmtQty(node.net.inbound)],
              ['To make', fmtQty(node.net.toMake)],
              ['Shortage', <span key="s" className={node.net.shortage > 0 ? 'font-semibold text-rose-300' : ''}>{fmtQty(node.net.shortage)}</span>],
              ['Excess', fmtQty(node.net.excess)],
            ]}
          />
        </Section>
      )}

      <Section title="Item master" hint="itm_getItem">
        {item.isPending ? (
          <Skeleton className="h-16" />
        ) : item.data ? (
          <KeyValues
            items={[
              ['Category', item.data.category ?? '–'],
              ['Class', item.data.itemClass],
              ['Stock unit', item.data.stockUomCode],
              ['Make / buy', item.data.makeBuy],
              ['Shelf life', item.data.shelfLifeDays ? `${item.data.shelfLifeDays} days` : '–'],
              ['Traceability', item.data.traceabilityType ?? '–'],
            ]}
          />
        ) : (
          <p className="text-[12.5px] text-ink-400">Not available.</p>
        )}
      </Section>

      {made && (bills.data?.length ?? 0) > 0 && (
        <Section title="Bills of this item" hint="bom_listBillRevisions">
          <div className="flex flex-col gap-1.5">
            {bills.data!.map((b) => (
              <button
                key={`${b.bomRef}-${b.bomRevision}`}
                onClick={() => navigate(`/bills/${encodeURIComponent(b.bomRef)}?rev=${encodeURIComponent(b.bomRevision)}`)}
                className="group flex cursor-pointer items-center gap-2.5 rounded-xl bg-white/[0.035] px-3 py-2 text-left ring-1 ring-white/[0.07] transition hover:bg-white/[0.07] ring-inset"
              >
                <Library className="size-4 text-violet-300" />
                <span className="font-mono text-[12.5px] text-ink-50">{b.bomRef}</span>
                <span className="text-[11px] text-ink-400">rev {b.bomRevision}</span>
                <span className="flex-1" />
                {b.isEffectiveNow && <span className="text-[10.5px] text-emerald-300">effective</span>}
                <StatusChip status={b.status} />
                <ArrowUpRight className="size-3.5 text-ink-500 group-hover:text-ink-200" />
              </button>
            ))}
          </div>
        </Section>
      )}

      <div className="flex flex-wrap gap-2">
        {line.level > 0 && made && (
          <Button size="sm" icon={<Network className="size-3.5" />} onClick={() => navigate(`/explode/${encodeURIComponent(line.item)}`)}>
            Explode from here
          </Button>
        )}
        <Button size="sm" icon={<GitFork className="size-3.5" />} onClick={() => navigate(`/where-used/${encodeURIComponent(line.item)}`)}>
          Where used
        </Button>
      </div>
    </motion.div>
  )
}

export function ExplosionSummary({
  tree,
  rootLine,
  onStudio,
  studioIcon,
  extra,
}: {
  tree: Tree
  rootLine: ExplosionLine
  onStudio?: () => void
  /** the studio button's icon (the kind of product it renders) */
  studioIcon?: ReactNode
  extra?: ReactNode
}) {
  const counts = new Map<string, number>()
  for (const n of tree.nodes) {
    const c = classify(n.line)
    counts.set(c, (counts.get(c) ?? 0) + 1)
  }
  return (
    <div className="flex flex-col gap-5">
      <div>
        <div className="text-[11px] font-semibold tracking-[0.14em] text-violet-300/90 uppercase">Product</div>
        <h2 className="mt-1.5 font-mono text-lg font-semibold break-all text-ink-50">{rootLine.item}</h2>
        <p className="mt-0.5 text-[13px] text-ink-300">{rootLine.description}</p>
      </div>
      {extra}
      <Section title="What it is made of" hint="occurrences by kind">
        <div className="flex flex-col gap-2">
          {[...counts.entries()]
            .filter(([c]) => c !== 'root')
            .map(([c, n]) => {
              const meta = CLASS_META[c as keyof typeof CLASS_META]
              return (
                <div key={c} className="flex items-center gap-3 text-[13px]">
                  <span className="size-2.5 rounded-full" style={{ background: meta.color, boxShadow: `0 0 10px ${meta.color}` }} />
                  <span className="text-ink-100">{meta.label}</span>
                  <span className="flex-1 truncate text-[11.5px] text-ink-500">{meta.hint}</span>
                  <span className="font-semibold text-ink-50 tabular">{n}</span>
                </div>
              )
            })}
        </div>
      </Section>
      <div className="rounded-2xl bg-white/[0.03] p-4 text-[12.5px] leading-relaxed text-ink-300 ring-1 ring-white/[0.07] ring-inset">
        <div className="mb-2 flex items-center gap-2 font-semibold text-ink-100">
          <Sparkles className="size-4 text-violet-300" /> Explore
        </div>
        <ul className="flex flex-col gap-1.5">
          <li>Click an occurrence to fly to it and see its API line.</li>
          <li>Double-click a made item to fold or unfold its branch.</li>
          <li>Drag to orbit, scroll to zoom, right-drag to pan.</li>
        </ul>
      </div>
      {onStudio && (
        <Button variant="warm" icon={studioIcon ?? <Cake className="size-4" />} onClick={onStudio}>
          Open in the product studio
        </Button>
      )}
      <div className="flex items-center gap-2 text-[11px] text-ink-500">
        <Boxes className="size-3.5" /> {tree.nodes.length} occurrences rebuilt from the engine order
        {tree.inferredCount > 0 && <span className="text-amber-300"> · {tree.inferredCount} placed by order (cut paths)</span>}
      </div>
    </div>
  )
}

export function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <section>
      <div className="mb-2.5 flex items-center gap-2">
        <h3 className="text-[11px] font-semibold tracking-[0.12em] text-ink-200 uppercase">{title}</h3>
        <span className="h-px flex-1 bg-white/[0.06]" />
        {hint && <span className="font-mono text-[10px] text-ink-500">{hint}</span>}
      </div>
      {children}
    </section>
  )
}

function Figure({ label, value, unit, title, accent }: { label: string; value: string; unit?: string; title?: string; accent?: string }) {
  return (
    <div className="relative overflow-hidden rounded-2xl bg-white/[0.04] px-3.5 py-3 ring-1 ring-white/[0.08] ring-inset" title={title}>
      {accent && <span className="absolute inset-x-0 top-0 h-px" style={{ background: `linear-gradient(90deg, transparent, ${accent}, transparent)` }} />}
      <div className="text-[10.5px] font-semibold tracking-[0.08em] text-ink-400 uppercase">{label}</div>
      <div className="mt-1 flex items-baseline gap-1.5">
        <span className="text-xl font-semibold tracking-tight text-ink-50 tabular">{value}</span>
        {unit && <span className="text-[11px] text-ink-400">{unit}</span>}
      </div>
    </div>
  )
}

export function toneOf(cls: string): string {
  return { root: 'pink', made: 'violet', bought: 'cyan', service: 'emerald', phantom: 'slate', byproduct: 'amber', note: 'zinc' }[cls] ?? 'slate'
}
