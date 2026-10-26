import { ChevronRight } from 'lucide-react'
import { useEffect, useMemo, useRef } from 'react'
import type { Tree, TreeNode } from '@/domain/tree'
import { CLASS_META, classify } from '@/domain/kinds'
import { exact, fmtMoney, fmtQty, fmtRate } from '@/domain/format'
import { cn } from '@/components/ui/primitives'
import type { ExplosionMode } from './explosionViz'

/** The indented reading of the same answer, in depth-first order, kept in step with the 3D selection and collapsing. */
export function ExplosionTable({
  tree,
  mode,
  collapsed,
  selectedId,
  onSelect,
  onToggle,
  currency,
}: {
  tree: Tree
  mode: ExplosionMode
  collapsed: Set<string>
  selectedId: string | null
  onSelect: (id: string) => void
  onToggle: (id: string) => void
  currency: string
}) {
  const rows = useMemo(() => {
    const out: TreeNode[] = []
    const walk = (n: TreeNode) => {
      out.push(n)
      if (!collapsed.has(n.id)) n.children.forEach(walk)
    }
    walk(tree.root)
    return out
  }, [tree, collapsed])
  const selectedRow = useRef<HTMLTableRowElement>(null)
  // block body: scrollIntoView may return a promise, which React would take for the effect's cleanup
  useEffect(() => {
    selectedRow.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
  }, [selectedId])

  return (
    <table className="w-full border-separate border-spacing-0 text-[12.5px]">
      <thead className="sticky top-0 z-10 bg-ink-900/95 backdrop-blur">
        <tr className="text-left text-[10.5px] font-semibold tracking-[0.08em] text-ink-400 uppercase">
          <th className="px-3 py-2.5">Occurrence</th>
          <th className="px-3 py-2.5">Description</th>
          <th className="px-3 py-2.5 text-right">Usage</th>
          <th className="px-3 py-2.5 text-right">Required</th>
          {mode === 'cost' && (
            <>
              <th className="px-3 py-2.5">Element</th>
              <th className="px-3 py-2.5 text-right">Unit cost</th>
              <th className="px-3 py-2.5 text-right">Extended</th>
            </>
          )}
          {mode === 'net' && (
            <>
              <th className="px-3 py-2.5 text-right">Stock used</th>
              <th className="px-3 py-2.5 text-right">To make</th>
              <th className="px-3 py-2.5 text-right">Shortage</th>
            </>
          )}
          <th className="px-3 py-2.5">Stop</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((n) => {
          const cls = classify(n.line)
          const selected = n.id === selectedId
          const hasKids = n.children.length > 0
          return (
            <tr
              key={n.id}
              ref={selected ? selectedRow : undefined}
              onClick={() => onSelect(n.id)}
              className={cn('cursor-pointer transition-colors', selected ? 'bg-violet-500/15' : 'hover:bg-white/[0.035]')}
            >
              <td className="border-t border-white/[0.05] px-3 py-1.5 whitespace-nowrap">
                <div className="flex items-center" style={{ paddingLeft: n.depth * 16 }}>
                  {hasKids ? (
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        onToggle(n.id)
                      }}
                      className="mr-1 flex size-5 cursor-pointer items-center justify-center rounded text-ink-400 hover:bg-white/10 hover:text-ink-100"
                      aria-label={collapsed.has(n.id) ? 'Expand' : 'Collapse'}
                    >
                      <ChevronRight className={cn('size-3.5 transition-transform', !collapsed.has(n.id) && 'rotate-90')} />
                    </button>
                  ) : (
                    <span className="mr-1 w-5" />
                  )}
                  <span className="mr-2 size-2 shrink-0 rounded-full" style={{ background: CLASS_META[cls].color, boxShadow: `0 0 8px ${CLASS_META[cls].color}` }} />
                  <span className="font-mono text-ink-50">{n.line.item}</span>
                  {n.line.revision.trim() && <span className="ml-1.5 text-[10.5px] text-ink-400">rev {n.line.revision}</span>}
                </div>
              </td>
              <td className="max-w-[260px] truncate border-t border-white/[0.05] px-3 py-1.5 text-ink-300">{n.line.description}</td>
              <td className="border-t border-white/[0.05] px-3 py-1.5 text-right whitespace-nowrap text-ink-300 tabular" title={exact(n.line.usage)}>
                {fmtQty(n.line.usage)} <span className="text-[10.5px] text-ink-500">{n.line.basis === 'FIXEDRUN' ? 'per run' : ''}</span>
              </td>
              <td className="border-t border-white/[0.05] px-3 py-1.5 text-right whitespace-nowrap text-ink-50 tabular" title={exact(n.line.required)}>
                {fmtQty(n.line.required)} <span className="text-[10.5px] text-ink-400">{n.line.unit}</span>
              </td>
              {mode === 'cost' && (
                <>
                  <td className="border-t border-white/[0.05] px-3 py-1.5 text-ink-300">{n.cost?.element || '–'}</td>
                  <td className="border-t border-white/[0.05] px-3 py-1.5 text-right text-ink-300 tabular" title={exact(n.cost?.unitCost)}>
                    {n.cost ? fmtRate(n.cost.unitCost, currency) : '–'}
                  </td>
                  <td className="border-t border-white/[0.05] px-3 py-1.5 text-right font-medium text-ink-50 tabular" title={exact(n.cost?.extendedCost)}>
                    {n.cost ? fmtMoney(n.cost.extendedCost, currency) : '–'}
                  </td>
                </>
              )}
              {mode === 'net' && (
                <>
                  <td className="border-t border-white/[0.05] px-3 py-1.5 text-right text-ink-300 tabular">{n.net ? fmtQty(n.net.stockUsed) : '–'}</td>
                  <td className="border-t border-white/[0.05] px-3 py-1.5 text-right text-ink-300 tabular">{n.net ? fmtQty(n.net.toMake) : '–'}</td>
                  <td className={cn('border-t border-white/[0.05] px-3 py-1.5 text-right tabular', n.net && n.net.shortage > 0 ? 'font-semibold text-rose-300' : 'text-ink-300')}>
                    {n.net ? fmtQty(n.net.shortage) : '–'}
                  </td>
                </>
              )}
              <td className="border-t border-white/[0.05] px-3 py-1.5 font-mono text-[11px] text-ink-400">{n.line.stopReason}</td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}
