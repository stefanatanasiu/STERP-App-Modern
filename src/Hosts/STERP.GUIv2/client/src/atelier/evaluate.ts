import { add, clamp01, compose, easeInOut, easeOutBack, lerp3, mulV, pose, rotZ, slerpM, smooth, translate, toSvg, apply, type Pose, type V3 } from './projection'
import type { ANode, AtelierModel, Box, Piece } from './types'

// Where everything is at a point of the timeline (build view) or in the assembled and exploded views. Pure functions of the
// model and the progress, so scrubbing backwards and forwards is exact.

export type View = 'build' | 'assembled' | 'exploded'

export interface PieceState {
  pose: Pose
  opacity: number
  /** flight or appearance progress, 0..1 (the artwork reads it as --m) */
  m: number
}

const HIDDEN = 0

/** One wheel turn when a wheel is finished: the build's spin test. */
function spinOf(model: AtelierModel, n: ANode, p: number): number {
  if ((n.assembly !== 'frontWheel' && n.assembly !== 'rearWheel') || !n.build) return 0
  const end = n.build[1] - 0.15 / model.beats
  const start = end - 1.05 / model.beats
  if (p <= start) return 0
  if (p >= end) return -Math.PI * 2
  return -Math.PI * 2 * easeInOut((p - start) / (end - start))
}

/** How a whole subassembly slides home onto its parent. */
function mountApproach(model: AtelierModel, n: ANode): V3 | null {
  const g = model.g
  switch (n.assembly) {
    case 'fork':
      return [-g.headDir[0] * 260, -g.headDir[1] * 260, 0]
    case 'cockpit':
      return [g.headDir[0] * 220, g.headDir[1] * 220, 0]
    case 'wheelset':
      return [0, -170, 0]
    case 'headset':
    case 'bb':
      return [0, 0, 160]
    default:
      return null
  }
}

/** The world pose of a node's bike frame: where it is built, then flying onto its parent, then wherever the parent is. */
export function homeAt(model: AtelierModel, n: ANode, p: number): Pose {
  if (n.role === 'root' || !n.owner || !n.home) return model.bayPose
  let base = n.home
  const spin = spinOf(model, n, p)
  if (spin !== 0) {
    const axle: V3 = n.assembly === 'rearWheel' ? [model.g.rear[0], model.g.rear[1], 0] : [model.g.front[0], model.g.front[1], 0]
    base = compose(base, compose(pose(axle, rotZ(spin)), pose([-axle[0], -axle[1], -axle[2]])))
  }
  const w = n.action
  if (!w || p <= w[0]) return base
  const target = homeAt(model, n.owner, p)
  if (p >= w[1]) return target
  const approach = mountApproach(model, n)
  return flight(base, target, (p - w[0]) / (w[1] - w[0]), approach ? mulV(target.r, approach) : null)
}

/** A flight from a to b: an arc up the mat, turning on the way, the last stretch along the approach with a little seat. */
function flight(a: Pose, b: Pose, u: number, approach: V3 | null): Pose {
  const pre = approach ? add(b.t, approach) : b.t
  const split = approach ? 0.68 : 1
  if (u < split) {
    const v = u / split
    // without an approach the part overshoots its seat a touch and settles
    const travel = approach ? easeInOut(v) : easeOutBack(v)
    const t = lerp3(a.t, pre, travel)
    const d = Math.hypot(pre[0] - a.t[0], pre[1] - a.t[1])
    const arc = Math.sin(Math.PI * v)
    return { r: slerpM(a.r, b.r, easeInOut(Math.min(1, v * 1.15))), t: [t[0], t[1] + arc * Math.min(420, 60 + d * 0.16), t[2] + arc * 40] }
  }
  const v = easeOutBack((u - split) / (1 - split))
  return { r: b.r, t: lerp3(pre, b.t, v) }
}

const window01 = (pc: Piece, p: number) => {
  const [w0, w1] = pc.window
  return w1 > w0 ? (p - w0) / (w1 - w0) : p >= w1 ? 1 : 0
}

/** Offset of a piece in the exploded view (bike coordinates): its own, plus its subassemblies'. */
export function explodeOf(model: AtelierModel, pc: Piece): V3 {
  const g = model.g
  let d: V3 = pc.explode ?? [0, 0, 0]
  for (let o = pc.node.owner; o && o.role !== 'root'; o = o.owner) {
    switch (o.assembly) {
      case 'fork':
        d = add(d, [-g.headDir[0] * 260 + 110, -g.headDir[1] * 260, 0])
        break
      case 'frontWheel':
        d = add(d, [460, -70, 0])
        break
      case 'rearWheel':
        d = add(d, [-460, -70, 0])
        break
      case 'cockpit':
        d = add(d, [160, 300, 0])
        break
      case 'rearTriangle':
        d = add(d, [-170, 30, 0])
        break
      case 'dropouts':
        d = add(d, [-60, -30, 0])
        break
    }
  }
  return d
}

export function pieceAt(model: AtelierModel, pc: Piece, p: number, view: View, packaging: boolean): PieceState {
  if (view !== 'build') {
    const shown = !!pc.seat && (pc.behavior === 'fly' || pc.behavior === 'appear') && (!pc.packaging || packaging)
    if (!shown) return { pose: pc.knolled, opacity: HIDDEN, m: 1 }
    const seat = view === 'exploded' ? translate(pc.seat!, explodeOf(model, pc)) : pc.seat!
    return { pose: compose(model.bayPose, seat), opacity: 1, m: 1 }
  }
  const u = window01(pc, p)
  if (!pc.seat) {
    if (pc.behavior === 'use') return { pose: pc.knolled, opacity: 1 - smooth(clamp01(u)) * 0.88, m: clamp01(u) }
    if (pc.behavior === 'emit') return { pose: pc.knolled, opacity: smooth(clamp01(u * 1.4)), m: clamp01(u) }
    return { pose: pc.knolled, opacity: 1, m: clamp01(u) }
  }
  const owner = pc.node.owner ?? model.root
  const home = homeAt(model, owner, p)
  const seat = compose(home, pc.seat)
  if (pc.behavior === 'appear') return { pose: seat, opacity: u <= 0 ? 0 : smooth(Math.min(1, u * 1.8)), m: clamp01(u) }
  if (u <= 0) return { pose: pc.knolled, opacity: 1, m: 0 }
  if (pc.behavior === 'absorb') {
    if (u >= 1) return { pose: seat, opacity: 0, m: 1 }
    return { pose: flight(pc.knolled, seat, u, null), opacity: u < 0.7 ? 1 : 1 - (u - 0.7) / 0.3, m: u }
  }
  if (u >= 1) return { pose: seat, opacity: 1, m: 1 }
  const approach = pc.approach ? mulV(home.r, pc.approach) : null
  return { pose: flight(pc.knolled, seat, u, approach), opacity: 1, m: u }
}

export type CoatVar = '--primer' | '--paint' | '--heat' | '--anod'

/** The coat and process variables of every piece they apply to. */
export function effectsAt(model: AtelierModel, p: number, view: View): Map<Piece, Partial<Record<CoatVar, number>>> {
  const out = new Map<Piece, Partial<Record<CoatVar, number>>>()
  for (const e of model.effects) {
    const u = view === 'build' ? clamp01((p - e.window[0]) / Math.max(1e-9, e.window[1] - e.window[0])) : 1
    let x0 = Infinity
    let x1 = -Infinity
    if (e.sweep)
      for (const pc of e.pieces) {
        x0 = Math.min(x0, pc.seat!.t[0])
        x1 = Math.max(x1, pc.seat!.t[0])
      }
    for (const pc of e.pieces) {
      let v: number
      if (e.variable === '--heat') v = view === 'build' ? Math.sin(Math.PI * u) : 0
      else if (e.sweep) {
        const o = x1 > x0 ? (pc.seat!.t[0] - x0) / (x1 - x0) : 0
        v = smooth(clamp01((u - o * 0.45) / 0.55))
      } else v = smooth(u)
      const vars = out.get(pc) ?? {}
      vars[e.variable] = v
      out.set(pc, vars)
    }
  }
  return out
}

/** The step running at p (index into model.steps); steps.length when the bike is finished. */
export function stepIndexAt(model: AtelierModel, p: number): number {
  if (p >= 1) return model.steps.length
  let k = 0
  for (let i = 0; i < model.steps.length; i++) if (model.steps[i].start <= p + 1e-9) k = i
  return k
}

/** The SVG box around the pieces shown in a view (assembled or exploded), with a margin. */
export function viewBox(model: AtelierModel, view: View, packaging: boolean): Box {
  let x0 = Infinity
  let y0 = Infinity
  let x1 = -Infinity
  let y1 = -Infinity
  for (const pc of model.pieces) {
    const s = pieceAt(model, pc, 1, view, packaging)
    if (s.opacity <= 0) continue
    for (const x of [pc.extent[0][0], pc.extent[1][0]])
      for (const y of [pc.extent[0][1], pc.extent[1][1]])
        for (const z of [pc.extent[0][2], pc.extent[1][2]]) {
          const [sx, sy] = toSvg(apply(s.pose, [x, y, z]))
          x0 = Math.min(x0, sx)
          y0 = Math.min(y0, sy)
          x1 = Math.max(x1, sx)
          y1 = Math.max(y1, sy)
        }
  }
  const m = 90
  return { x: x0 - m, y: y0 - m, w: x1 - x0 + 2 * m, h: y1 - y0 + 2 * m }
}
