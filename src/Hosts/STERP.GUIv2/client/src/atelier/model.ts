import type { Tree } from '@/domain/tree'
import { shortName } from '@/components/bom/studioModel'
import { assemblyOf, COCKPIT_LYING, diameterIn, recipeFor, roleOf, serviceIcon, text, type ArtKind, type AssemblyKind, type Ctx, type ItemRecipe } from './catalog'
import { bikeGeometry } from './geometry'
import { CAPTION, layoutMat } from './layout'
import { apply, fromSvg, I3, mulV, ORIGIN, pose, toSvg, type M3, type Pose, type V3 } from './projection'
import { bikeSpecOf } from './spec'
import { actionsOf, schedule } from './timeline'
import type { ANode, AtelierModel, Effect, Piece } from './types'

// Builds the atelier from one explosion (bom_explodeBill INDENTED, with the costed reading overlaid): every occurrence becomes a
// node, every leaf line its pieces, the mat is laid out and the timeline scheduled. Quantities and costs stay the API's: the
// model only reads counts (usage per parent) and sizes (descriptions) to draw.

const FAMILY: Record<AssemblyKind, string> = {
  bike: '#f9a8d4',
  frame: '#a78bfa',
  frontTriangle: '#c084fc',
  rearTriangle: '#818cf8',
  chainstays: '#93c5fd',
  seatstays: '#a5b4fc',
  dropouts: '#94a3b8',
  headset: '#f0abfc',
  bb: '#e879f9',
  fork: '#60a5fa',
  wheelset: '#22d3ee',
  frontWheel: '#22d3ee',
  rearWheel: '#2dd4bf',
  cockpit: '#fbbf24',
  other: '#cbd5e1',
}

const ART_COLOR: Partial<Record<ArtKind, string>> = {
  crankset: '#fb923c',
  chain: '#fdba74',
  quickLink: '#fdba74',
  derailleur: '#fb923c',
  pedal: '#fdba74',
  caliper: '#fb7185',
  coil: '#f472b6',
  bolt: '#cbd5e1',
  washer: '#cbd5e1',
  nut: '#cbd5e1',
  saddle: '#a3e635',
  seatpost: '#bef264',
  tyre: '#94a3b8',
  innerTube: '#f87171',
  mudguard: '#38bdf8',
  rack: '#34d399',
  lamp: '#fef08a',
  kickstand: '#bef264',
  bell: '#fde047',
  reflector: '#fca5a5',
  cage: '#67e8f9',
  cartridge: '#93c5fd',
  carton: '#e9d5ff',
  foam: '#e9d5ff',
  label: '#e9d5ff',
  manual: '#e9d5ff',
  note: '#64748b',
  part: '#c4b5fd',
}

const COUNTED = new Set(['EA', 'PR', 'SET', 'PCS', 'PC', 'NO'])

function countOf(line: Ctx['line']): number {
  return COUNTED.has(line.unit.trim().toUpperCase()) ? Math.max(1, Math.min(60, Math.round(line.usage))) : 1
}

/** "10:FG-FRAME-GRAVEL-M" at the end of the path -> "10". */
function findOf(path: string): string {
  const last = path.trimEnd().split('/').pop() ?? ''
  const i = last.indexOf(':')
  return i > 0 ? last.slice(0, i) : ''
}

/** The pose its bike frame lies in while a subassembly is built: the cockpit on its back, everything else as on the bike. */
const restOf = (a: AssemblyKind): M3 => (a === 'cockpit' ? COCKPIT_LYING : I3)

/** The SVG box of a bike-coordinate box turned by r. */
function projectedBox(b: { min: V3; max: V3 }, r: M3) {
  let x0 = Infinity
  let y0 = Infinity
  let x1 = -Infinity
  let y1 = -Infinity
  for (const x of [b.min[0], b.max[0]])
    for (const y of [b.min[1], b.max[1]])
      for (const z of [b.min[2], b.max[2]]) {
        const [sx, sy] = toSvg(mulV(r, [x, y, z]))
        x0 = Math.min(x0, sx)
        y0 = Math.min(y0, sy)
        x1 = Math.max(x1, sx)
        y1 = Math.max(y1, sy)
      }
  return { x0, y0, x1, y1, w: x1 - x0, h: y1 - y0 }
}

/** A pose that puts the centre of a bike-coordinate box (turned by r) on an SVG point of the mat. */
function placeOnMat(b: { min: V3; max: V3 }, r: M3, cx: number, cy: number): Pose {
  const pb = projectedBox(b, r)
  return pose(fromSvg(cx - (pb.x0 + pb.x1) / 2, cy - (pb.y0 + pb.y1) / 2, 0), r)
}

export function buildAtelier(tree: Tree): AtelierModel {
  const spec = bikeSpecOf(tree)
  const g = bikeGeometry(spec)

  const nodes: ANode[] = tree.nodes.map((t) => ({
    id: t.id,
    tree: t,
    line: t.line,
    role: roleOf(t.line),
    name: shortName(t.line.description),
    color: '',
    depth: t.depth,
    find: findOf(t.line.path),
    parent: null,
    children: [],
    owner: null,
    assembly: 'other',
    pieces: [],
    path: '',
  }))
  const byId = new Map(nodes.map((n) => [n.id, n]))
  for (const n of nodes) {
    const p = n.tree.parent ? byId.get(n.tree.parent.id) : undefined
    if (p) {
      n.parent = p
      p.children.push(n)
    }
  }
  const root = nodes[0]
  for (const n of nodes) {
    // a made line the engine did not explode (use stock, depth limit) is drawn as a bought-in part
    if (n.role === 'make' && n.children.length === 0) n.role = 'part'
    let o = n.parent
    while (o && o.role === 'phantom') o = o.parent
    n.owner = o
    n.assembly = n.role === 'make' || n.role === 'root' ? assemblyOf(n.line) : 'other'
    const chain: string[] = []
    for (let a: ANode | null = n; a; a = a.parent) chain.push(a.id)
    n.path = chain.join(' ')
  }

  // the pieces of every leaf line
  const sizes = new Map<string, ItemRecipe['size']>()
  for (const n of nodes) {
    if (n.role === 'root' || n.role === 'make' || n.role === 'phantom') continue
    const owner = n.owner ?? root
    const stocks = n.role === 'stock' ? actionsOf(owner).filter((s) => s.role === 'stock') : []
    stocks.sort((a, b) => diameterIn(b.line.description, 0) - diameterIn(a.line.description, 0))
    const ctx: Ctx = { g, spec, node: n.tree, line: n.line, owner: owner.assembly, count: countOf(n.line), stockIndex: Math.max(0, stocks.indexOf(n)), stockCount: stocks.length }
    const recipe = recipeFor(ctx, n.name)
    sizes.set(n.id, recipe.size)
    n.pieces = recipe.pieces.map((s) => ({ ...s, id: `${n.id}:${s.key}`, node: n, knolled: ORIGIN, window: [0, 0] }))
  }

  // colours: subassemblies by family, loose lines by what they are
  for (const n of nodes) {
    const art = n.pieces[0]?.art
    n.color =
      n.role === 'make' || n.role === 'root'
        ? FAMILY[n.assembly]
        : n.role === 'service'
          ? '#34d399'
          : n.role === 'byproduct'
            ? '#fbbf24'
            : n.owner && n.owner.role !== 'root'
              ? FAMILY[n.owner.assembly]
              : ((art && ART_COLOR[art]) ?? '#cbd5e1')
  }

  // assembled bounds of every subassembly and of the bike (bike coordinates)
  for (const n of nodes) {
    for (const pc of n.pieces) {
      if (!pc.seat || (pc.behavior !== 'fly' && pc.behavior !== 'appear')) continue
      for (let o = n.owner; o; o = o.owner) {
        const b = (o.bounds ??= { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] })
        for (const x of [pc.extent[0][0], pc.extent[1][0]])
          for (const y of [pc.extent[0][1], pc.extent[1][1]])
            for (const z of [pc.extent[0][2], pc.extent[1][2]]) {
              const w = apply(pc.seat, [x, y, z])
              b.min = [Math.min(b.min[0], w[0]), Math.min(b.min[1], w[1]), Math.min(b.min[2], w[2])]
              b.max = [Math.max(b.max[0], w[0]), Math.max(b.max[1], w[1]), Math.max(b.max[2], w[2])]
            }
      }
    }
  }
  for (const n of nodes) if ((n.role === 'make' || n.role === 'root') && !n.bounds) n.bounds = { min: [-150, -100, 0], max: [150, 100, 0] }

  // the mat
  const { mat, bay } = layoutMat(root, {
    areaSize: (n) => {
      const pb = projectedBox(n.bounds!, restOf(n.assembly))
      const margin = n.role === 'root' ? 200 : 50
      return [Math.max(260, pb.w + margin * 2), Math.max(200, pb.h + margin * 2)]
    },
    slotSize: (n) => {
      const [w, h] = sizes.get(n.id) ?? [160, 100]
      return [Math.max(w, 170), h + CAPTION]
    },
  })
  const bayPose = placeOnMat(root.bounds!, I3, bay.x + bay.w / 2, bay.y + bay.h / 2)
  for (const n of nodes) {
    if (n.role === 'make' && n.area) n.home = placeOnMat(n.bounds!, restOf(n.assembly), n.area.x + n.area.w / 2, n.area.y + n.area.h / 2)
    if (!n.slot) continue
    for (const pc of n.pieces) pc.knolled = pose(fromSvg(n.slot.x + pc.lie.x, n.slot.y + pc.lie.y, pc.lie.z ?? 0), pc.lie.r ?? I3)
  }
  // an unrecognised part goes into the middle of what it is built into
  for (const n of nodes)
    for (const pc of n.pieces)
      if (pc.behavior === 'absorb' && !pc.seat) {
        const b = n.owner?.bounds ?? root.bounds!
        pc.seat = pose([(b.min[0] + b.max[0]) / 2, (b.min[1] + b.max[1]) / 2, 0])
      }

  const { steps, beats } = schedule(root, mat, bay)

  // piece windows: one after another across the line's window when there are several
  for (const n of nodes) {
    if (!n.action) continue
    const [a0, a1] = n.action
    const len = a1 - a0
    const staggered = n.pieces.filter((p) => p.order !== undefined && p.order > 0).length > 0
    for (const pc of n.pieces) {
      if (staggered && pc.behavior !== 'stay' && pc.behavior !== 'use') {
        const s = a0 + (pc.order ?? 0) * 0.55 * len
        pc.window = [s, s + 0.45 * len]
      } else pc.window = [a0, a1]
    }
  }
  // a by-product comes out of the cutting: the window of the first stock line of its owner
  for (const n of nodes) {
    if (n.role !== 'byproduct' || !n.owner) continue
    const cut = actionsOf(n.owner).find((s) => s.role === 'stock')
    if (cut?.action) for (const pc of n.pieces) pc.window = cut.action
  }

  // coats and processes: primer and paint over everything painted in the subassembly, heat treatment, anodising
  const effects: Effect[] = []
  const inside = (o: ANode) => nodes.filter((n) => n.path.split(' ').includes(o.id)).flatMap((n) => n.pieces)
  for (const n of nodes) {
    if (!n.action || !n.owner) continue
    const t = text(n.line)
    const art = n.pieces[0]?.art
    const coated = () => inside(n.owner!).filter((p) => p.coat && (p.behavior === 'fly' || p.behavior === 'appear'))
    if (art === 'tin') effects.push({ variable: /PRIMER/.test(t) ? '--primer' : '--paint', pieces: coated(), window: n.action, sweep: true })
    if (n.role === 'service') {
      const icon = serviceIcon(t)
      if (icon === 'heat') effects.push({ variable: '--heat', pieces: coated(), window: n.action, sweep: false })
      if (icon === 'anodise') effects.push({ variable: '--anod', pieces: inside(n.owner).filter((p) => p.art === 'dropout' || p.art === 'bracket'), window: n.action, sweep: false })
    }
  }

  const pieces: Piece[] = nodes.flatMap((n) => n.pieces)
  return { spec, g, root, nodes, byId, pieces, steps, effects, mat, bay, bayPose, beats }
}
