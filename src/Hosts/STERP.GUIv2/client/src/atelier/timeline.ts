import { phantomLeaves } from './layout'
import type { ANode, Box, Step } from './types'

// The build as a timeline driven by the explosion: subassemblies are built bottom up (the bill's post-order), each in its own
// step where its lines come in one after another in find-number order; phantom kits open into their parent's step; the bike's
// final assembly fits its lines one by one. Windows are in beats here and normalised to 0..1 at the end.

const INTRO = 1.8
const LEAD = 0.5
const TAIL = 0.45
const SPIN = 1.2
const OVERLAP = 0.62

function beatsOf(n: ANode): number {
  const art = n.pieces[0]?.art
  switch (n.role) {
    case 'make':
      return 1.5
    case 'stock':
      return 1.8
    case 'service':
      return 1.3
    case 'note':
    case 'byproduct':
      return 0.7
    case 'supply':
      return art === 'tin' ? 1.7 : art === 'spool' ? 1.4 : art === 'coil' ? 1.5 : 1.2
    default: {
      const moving = n.pieces.filter((p) => p.behavior === 'fly' || p.behavior === 'appear').length
      return moving > 1 ? 1.15 + Math.min(1.5, 0.045 * (moving - 1)) : n.pieces.some((p) => p.packaging) ? 1.1 : 1.15
    }
  }
}

/** A made node's lines in build order: phantom kits open in place. */
export function actionsOf(n: ANode): ANode[] {
  return n.children.flatMap((c) => (c.role === 'phantom' ? actionsOf(c) : [c]))
}

const lower = (s: string) => (s.length > 1 && s[1] === s[1].toLowerCase() ? s[0].toLowerCase() + s.slice(1) : s)
/** "Front wheel 700C built" -> "Front wheel 700C"; "Headset assembly pressed" -> "Headset assembly". */
const clean = (s: string) => s.replace(/\s+(built|pressed|welded|machined|assembled)$/i, '')

function verbOf(n: ANode): string {
  const art = n.pieces[0]?.art
  if (n.role === 'make') return `Fitting the ${lower(clean(n.name))}`
  if (n.role === 'service') return n.name
  if (n.role === 'note') return 'Work instruction'
  if (art === 'cartridge') return 'Greasing the bearings and threads'
  if (art === 'coil') return `Routing the ${lower(n.name)}`
  if (art === 'carton' || art === 'foam' || art === 'label' || art === 'manual') return `Packing: ${lower(n.name)}`
  return `Fitting the ${lower(n.name)}`
}

export function union(...boxes: (Box | undefined)[]): Box {
  const bs = boxes.filter((b): b is Box => !!b)
  const x0 = Math.min(...bs.map((b) => b.x))
  const y0 = Math.min(...bs.map((b) => b.y))
  const x1 = Math.max(...bs.map((b) => b.x + b.w))
  const y1 = Math.max(...bs.map((b) => b.y + b.h))
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 }
}

/** Where a line comes from on the mat: its cell, its tray or its slot. */
export function sourceOf(n: ANode): Box | undefined {
  if (n.cell) return n.cell
  if (n.slot) return n.slot
  if (n.tray) return n.tray
  const leaves = n.role === 'phantom' ? phantomLeaves(n) : []
  return leaves.length ? union(...leaves.map((l) => l.slot)) : undefined
}

/** Assigns every node's windows and returns the steps and the length of the timeline in beats. */
export function schedule(root: ANode, mat: Box, bay: Box): { steps: Step[]; beats: number } {
  const steps: Step[] = [{ id: 'intro', node: root, title: 'Everything in the explosion, laid out', start: 0, end: INTRO, color: root.color, camera: mat }]
  let beat = INTRO

  const visit = (s: ANode) => {
    for (const c of s.children) if (c.role === 'make') visit(c)
    for (const c of s.children) if (c.role === 'phantom') for (const d of c.children) if (d.role === 'make') visit(d)
    const start = beat
    let t = start + LEAD
    let end = t
    for (const a of actionsOf(s)) {
      const d = beatsOf(a)
      a.action = [t, t + d]
      end = Math.max(end, t + d)
      if (s.role === 'root') steps.push({ id: `fit-${a.id}`, node: a, title: verbOf(a), start: t, end: t + d, color: a.color, camera: union(bay, sourceOf(a)) })
      t += d * OVERLAP
    }
    end += TAIL + (s.assembly === 'frontWheel' || s.assembly === 'rearWheel' ? SPIN : 0)
    s.build = [start, end]
    // a subassembly's step looks at its build area and the lines laid out beside it; its own subassemblies fly in
    const near = actionsOf(s).filter((a) => a.role !== 'make').map((a) => a.slot)
    if (s.role !== 'root') steps.push({ id: `build-${s.id}`, node: s, title: `Building the ${lower(clean(s.name))}`, start, end, color: s.color, camera: union(s.area ?? s.cell ?? mat, ...near) })
    beat = end
  }
  visit(root)

  const total = beat
  const norm = (w: [number, number]): [number, number] => [w[0] / total, w[1] / total]
  const all: ANode[] = []
  const walk = (n: ANode) => {
    all.push(n)
    n.children.forEach(walk)
  }
  walk(root)
  for (const n of all) {
    if (n.action) n.action = norm(n.action)
    if (n.build) n.build = norm(n.build)
  }
  for (const s of steps) {
    s.start /= total
    s.end /= total
  }
  steps.sort((a, b) => a.start - b.start)
  return { steps, beats: total }
}
