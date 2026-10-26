import type { CostedLine, ExplosionLine, NettedLine, WhereUsedRow } from '@/api/types'

// Rebuilds the parent/child structure of one explosion answer for display. The API returns the occurrences in engine order:
// BOMEXPL records all children of a node as one block, then expands each made or phantom child depth first. Each line carries
// its level and occurrence path ("ROOT/10:CHILD/20:GRANDCHILD", by-products "BP10:"), cut by the API at 80 characters.
// Nothing here calculates a quantity or a cost: the nodes hold the API lines unchanged.

export const PATH_LIMIT = 80

const EXPANDED_STOPS = new Set(['ROOT', 'MAKE', 'PHANTOM'])

/** The fields the structure needs; explosion and costed lines both have them (costed lines have no stop reason). */
export interface StructureLine {
  level: number
  item: string
  kind: string
  path: string
  stopReason?: string
}

export interface TreeNode<L extends StructureLine = ExplosionLine> {
  id: string
  /** Position in the API answer (engine order). */
  seq: number
  line: L
  depth: number
  parent: TreeNode<L> | null
  children: TreeNode<L>[]
  /** The API cut the path before the parent could be told apart: the position follows the engine order only. */
  inferred: boolean
  cost?: CostedLine
  net?: NettedLine
}

export interface Tree<L extends StructureLine = ExplosionLine> {
  root: TreeNode<L>
  nodes: TreeNode<L>[]
  byId: Map<string, TreeNode<L>>
  inferredCount: number
}

const clean = (path: string) => (path ?? '').trimEnd()

/** A made or phantom occurrence the engine went on to explode (its children follow later as one block). */
export function isExpanded(line: StructureLine): boolean {
  const kind = line.kind.trim()
  if (line.level === 0) return true
  if (kind !== 'INTERMED' && kind !== 'PHANTOM') return false
  return line.stopReason === undefined || EXPANDED_STOPS.has(line.stopReason.trim())
}

/** 'yes' when the child's path extends the parent's, 'maybe' when both were cut at the limit, 'no' otherwise. */
export function childVerdict(parent: StructureLine, line: StructureLine): 'yes' | 'maybe' | 'no' {
  if (line.level !== parent.level + 1) return 'no'
  const parentPath = clean(parent.path)
  const path = clean(line.path)
  if (parentPath.length < PATH_LIMIT) return path.startsWith(parentPath + '/') ? 'yes' : 'no'
  return path === parentPath.slice(0, PATH_LIMIT) ? 'maybe' : 'no'
}

export function buildTree<L extends StructureLine>(lines: L[]): Tree<L> | null {
  if (lines.length === 0) return null
  const nodes: TreeNode<L>[] = lines.map((line, seq) => ({
    id: `n${seq}`,
    seq,
    line,
    depth: line.level,
    parent: null,
    children: [],
    inferred: false,
  }))
  const root = nodes[0]
  let next = 1

  const attach = (parent: TreeNode<L>, child: TreeNode<L>, inferred: boolean) => {
    child.parent = parent
    child.inferred = inferred || parent.inferred
    parent.children.push(child)
  }

  const readBlock = (parent: TreeNode<L>) => {
    while (next < nodes.length) {
      const verdict = childVerdict(parent.line, nodes[next].line)
      if (verdict === 'no') break
      attach(parent, nodes[next], verdict === 'maybe')
      next++
    }
    for (const child of [...parent.children]) {
      if (next >= nodes.length) return
      if (isExpanded(child.line) && childVerdict(child.line, nodes[next].line) !== 'no') readBlock(child)
    }
  }
  readBlock(root)

  // an answer in an unexpected order: place the rest under the nearest earlier occurrence one level up whose path agrees
  for (; next < nodes.length; next++) {
    const node = nodes[next]
    let parent: TreeNode<L> | null = null
    for (let k = next - 1; k >= 0 && !parent; k--) {
      if (childVerdict(nodes[k].line, node.line) === 'yes') parent = nodes[k]
    }
    for (let k = next - 1; k >= 0 && !parent; k--) {
      if (nodes[k].depth === node.depth - 1) parent = nodes[k]
    }
    attach(parent ?? root, node, true)
  }

  const byId = new Map(nodes.map((n) => [n.id, n]))
  return { root, nodes, byId, inferredCount: nodes.filter((n) => n.inferred).length }
}

/**
 * Attaches the costed reading (bom_costBill) to the explosion nodes. Both answers come from the same engine walk, so the trees
 * are paired node by node; a node whose costed twin is missing simply has no cost. Returns the number of nodes paired.
 */
export function overlayCosts(tree: Tree, costed: CostedLine[]): number {
  const costTree = buildTree(costed)
  if (!costTree) return 0
  let paired = 0
  const zip = (a: TreeNode, b: TreeNode<CostedLine>) => {
    if (a.line.item !== b.line.item || a.depth !== b.depth) return
    a.cost = b.line
    paired++
    const used = new Set<TreeNode<CostedLine>>()
    for (const child of a.children) {
      const twin = b.children.find((c) => !used.has(c) && c.line.item === child.line.item)
      if (!twin) continue
      used.add(twin)
      zip(child, twin)
    }
  }
  zip(tree.root, costTree.root)
  return paired
}

/**
 * Attaches the netted reading (bom_netBillRequirement), which carries no paths: its lines follow the same engine order, without
 * by-products and without the children of occurrences the netting did not expand (taken from stock). The tree is walked in
 * that order and each netted line is given to the occurrence it describes. Returns the number of netted lines placed.
 */
export function overlayNet(tree: Tree, netted: NettedLine[]): { placed: number; complete: boolean } {
  let next = 0
  const take = (node: TreeNode): boolean => {
    const line = netted[next]
    if (!line || line.level !== node.depth || line.item !== node.line.item) return false
    node.net = line
    next++
    return true
  }
  const visit = (node: TreeNode) => {
    const block: TreeNode[] = []
    for (const child of node.children) {
      if (take(child)) block.push(child)
    }
    for (const child of block) {
      if (child.net && EXPANDED_STOPS.has(child.net.stopReason.trim()) && child.children.length > 0) visit(child)
    }
  }
  if (take(tree.root)) visit(tree.root)
  return { placed: next, complete: next === netted.length }
}

export function ancestors<L extends StructureLine>(node: TreeNode<L>): TreeNode<L>[] {
  const chain: TreeNode<L>[] = []
  for (let n = node.parent; n; n = n.parent) chain.push(n)
  return chain
}

export function descendants<L extends StructureLine>(node: TreeNode<L>): TreeNode<L>[] {
  const out: TreeNode<L>[] = []
  const walk = (n: TreeNode<L>) => {
    for (const c of n.children) {
      out.push(c)
      walk(c)
    }
  }
  walk(node)
  return out
}

/** Depth-first display order (the indented reading a person expects), as opposed to the engine's block order. */
export function displayOrder<L extends StructureLine>(root: TreeNode<L>): TreeNode<L>[] {
  const out: TreeNode<L>[] = []
  const walk = (n: TreeNode<L>) => {
    out.push(n)
    for (const c of n.children) walk(c)
  }
  walk(root)
  return out
}

// ---------------------------------------------------------------- where used (bom_getWhereUsed)

export interface UseNode {
  id: string
  item: string
  depth: number
  parent: UseNode | null
  children: UseNode[]
  /** The where-used rows that reach this parent: one per bill (a parent item can use the component through several bills). */
  rows: WhereUsedRow[]
}

/** Each row's pathText ("component < parent < grandparent") is one upward chain; chains sharing a prefix share nodes. */
export function buildWhereUsedTree(component: string, rows: WhereUsedRow[]): { root: UseNode; nodes: UseNode[] } {
  const root: UseNode = { id: 'u0', item: component, depth: 0, parent: null, children: [], rows: [] }
  const nodes: UseNode[] = [root]
  for (const row of rows) {
    const chain = row.pathText.split('<').map((s) => s.trim()).filter(Boolean)
    let at = root
    for (let d = 1; d < chain.length; d++) {
      let child = at.children.find((c) => c.item === chain[d])
      if (!child) {
        child = { id: `u${nodes.length}`, item: chain[d], depth: d, parent: at, children: [], rows: [] }
        at.children.push(child)
        nodes.push(child)
      }
      if (d === chain.length - 1) child.rows.push(row)
      at = child
    }
  }
  return { root, nodes }
}
