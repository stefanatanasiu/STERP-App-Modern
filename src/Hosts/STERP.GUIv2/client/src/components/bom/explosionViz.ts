import type { Tree, TreeNode } from '@/domain/tree'
import { classify } from '@/domain/kinds'
import { fmtMoney, fmtQty } from '@/domain/format'
import type { VizNode } from '@/three/tree/viz'

export type ExplosionMode = 'structure' | 'cost' | 'net' | 'materials'

/** Maps the explosion occurrences onto the 3D nodes. Sizes and figures come from the API lines as they are. */
export function toViz(tree: Tree, mode: ExplosionMode, currency: string): VizNode {
  let maxCost = 0
  if (mode === 'cost') for (const n of tree.nodes) if (n.cost && n.depth > 0) maxCost = Math.max(maxCost, n.cost.extendedCost)
  const map = (n: TreeNode, parent: VizNode | null): VizNode => {
    const v: VizNode = {
      id: n.id,
      depth: n.depth,
      parent,
      children: [],
      cls: classify(n.line),
      code: n.line.item,
      title: n.line.description,
      figure: figure(n, mode, currency),
      weight: mode === 'cost' && n.cost && maxCost > 0 ? n.cost.extendedCost / maxCost : undefined,
      ring: mode === 'net' && n.net ? (n.net.shortage > 0 ? 'shortage' : n.net.stockUsed > 0 ? 'covered' : null) : null,
      flagged: !!n.line.warning?.trim(),
    }
    v.children = n.children.map((c) => map(c, v))
    return v
  }
  return map(tree.root, null)
}

function figure(n: TreeNode, mode: ExplosionMode, currency: string): string | undefined {
  if (mode === 'cost' && n.cost) return fmtMoney(n.cost.extendedCost, currency)
  if (mode === 'net' && n.net) return n.net.shortage > 0 ? `short ${fmtQty(n.net.shortage, true)}` : `${fmtQty(n.net.required, true)} ${n.net.unit}`
  if (n.line.kind.trim() === 'NOTE') return undefined
  return `${fmtQty(n.line.required, true)} ${n.line.unit}`
}
