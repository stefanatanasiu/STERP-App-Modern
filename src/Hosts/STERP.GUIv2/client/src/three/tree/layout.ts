// Positions for the 3D trees. Pure geometry over the visible structure (collapsed branches hidden).

export type LayoutKind = 'cone' | 'orbit' | 'tiers'

export type Vec3 = [number, number, number]

export interface LayoutNode {
  id: string
  depth: number
  children: LayoutNode[]
}

export interface LayoutResult {
  positions: Map<string, Vec3>
  radius: number
  center: Vec3
  minY: number
}

const LEVEL_GAP = 4.6
const LEAF_RADIUS = 1.05
const SIBLING_GAP = 0.7

function visibleChildren(n: LayoutNode, collapsed: Set<string>): LayoutNode[] {
  return collapsed.has(n.id) ? [] : n.children
}

/**
 * Cone tree (Robertson, Mackinlay and Card 1991): each parent sits at the apex of a cone whose base circle carries its
 * children, one level lower; the circle is sized so that the children's own cones never overlap.
 */
function cone(root: LayoutNode, collapsed: Set<string>, out: Map<string, Vec3>, direction: 1 | -1) {
  const footprint = new Map<string, number>()
  const ring = new Map<string, number>()
  const measure = (n: LayoutNode): number => {
    const kids = visibleChildren(n, collapsed)
    if (kids.length === 0) {
      footprint.set(n.id, LEAF_RADIUS)
      return LEAF_RADIUS
    }
    const radii = kids.map(measure)
    if (kids.length === 1) {
      ring.set(n.id, 0)
      const f = Math.max(LEAF_RADIUS, radii[0])
      footprint.set(n.id, f)
      return f
    }
    const circumference = radii.reduce((s, r) => s + 2 * r + SIBLING_GAP, 0)
    const r = Math.max(2.2, circumference / (2 * Math.PI))
    ring.set(n.id, r)
    const f = r + Math.max(...radii)
    footprint.set(n.id, f)
    return f
  }
  measure(root)
  const place = (n: LayoutNode, at: Vec3, phase: number) => {
    out.set(n.id, at)
    const kids = visibleChildren(n, collapsed)
    if (kids.length === 0) return
    const r = ring.get(n.id) ?? 0
    const total = kids.reduce((s, k) => s + 2 * footprint.get(k.id)! + SIBLING_GAP, 0)
    let run = 0
    for (const k of kids) {
      const span = 2 * footprint.get(k.id)! + SIBLING_GAP
      const angle = phase + ((run + span / 2) / total) * Math.PI * 2
      run += span
      place(k, [at[0] + Math.cos(angle) * r, at[1] - LEVEL_GAP * direction, at[2] + Math.sin(angle) * r], angle + Math.PI * 0.62)
    }
  }
  place(root, [0, 0, 0], -Math.PI / 2)
}

/** Orbit: the product at the centre, each level on a wider ring, sectors sized by the number of leaves below. */
function orbit(root: LayoutNode, collapsed: Set<string>, out: Map<string, Vec3>) {
  const leaves = new Map<string, number>()
  const count = (n: LayoutNode): number => {
    const kids = visibleChildren(n, collapsed)
    const c = kids.length === 0 ? 1 : kids.reduce((s, k) => s + count(k), 0)
    leaves.set(n.id, c)
    return c
  }
  count(root)
  const place = (n: LayoutNode, depth: number, from: number, to: number) => {
    const mid = (from + to) / 2
    const radius = depth === 0 ? 0 : 4.2 + (depth - 1) * 4.4
    const lift = depth === 0 ? 0 : Math.sin(mid * 3 + depth) * 0.9 - depth * 0.35
    out.set(n.id, [Math.cos(mid) * radius, lift, Math.sin(mid) * radius])
    const kids = visibleChildren(n, collapsed)
    const total = leaves.get(n.id)!
    let at = from
    for (const k of kids) {
      const share = ((to - from) * leaves.get(k.id)!) / total
      place(k, depth + 1, at, at + share)
      at += share
    }
  }
  place(root, 0, 0, Math.PI * 2)
}

/** Tiers: a tidy tree in one plane, levels as horizontal tiers, gently curved towards the viewer. */
function tiers(root: LayoutNode, collapsed: Set<string>, out: Map<string, Vec3>, direction: 1 | -1) {
  let cursor = 0
  const xs = new Map<string, number>()
  const walk = (n: LayoutNode): number => {
    const kids = visibleChildren(n, collapsed)
    if (kids.length === 0) {
      const x = cursor
      cursor += 2.3
      xs.set(n.id, x)
      return x
    }
    const childXs = kids.map(walk)
    const x = (childXs[0] + childXs[childXs.length - 1]) / 2
    xs.set(n.id, x)
    return x
  }
  walk(root)
  const width = Math.max(1, cursor - 2.3)
  const half = width / 2
  const bend = Math.min(0.9, 26 / Math.max(width, 1))
  const set = (n: LayoutNode) => {
    const x = xs.get(n.id)! - half
    out.set(n.id, [x, -n.depth * 3.6 * direction, -(x * x) * 0.012 * bend])
    for (const k of visibleChildren(n, collapsed)) set(k)
  }
  set(root)
}

/** direction -1 grows the tree upwards (where used: from a component up to the products that use it). */
export function layoutTree(root: LayoutNode, kind: LayoutKind, collapsed: Set<string>, direction: 1 | -1 = 1): LayoutResult {
  const positions = new Map<string, Vec3>()
  if (kind === 'cone') cone(root, collapsed, positions, direction)
  else if (kind === 'orbit') orbit(root, collapsed, positions)
  else tiers(root, collapsed, positions, direction)
  const min: Vec3 = [Infinity, Infinity, Infinity]
  const max: Vec3 = [-Infinity, -Infinity, -Infinity]
  for (const p of positions.values()) {
    for (let i = 0; i < 3; i++) {
      min[i] = Math.min(min[i], p[i])
      max[i] = Math.max(max[i], p[i])
    }
  }
  const center: Vec3 = [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2]
  let radius = 2
  for (const p of positions.values()) radius = Math.max(radius, Math.hypot(p[0] - center[0], p[1] - center[1], p[2] - center[2]))
  return { positions, radius: radius + 1.6, center, minY: min[1] }
}
