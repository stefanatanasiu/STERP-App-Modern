import type { BikeSpec } from './spec'
import type { V3 } from './projection'

// The bicycle in millimetres, seen from its drive side: x forward, y up, z toward the viewer (the drive side). The bottom bracket
// sits on x = 0 and the tyres on y = 0. A medium commuter frame with 700C wheels: 1030 mm wheelbase, 425 mm chainstays, 70 mm
// bottom bracket drop, 73.5 degree seat and 71.5 degree head angles. Wheel, tyre, crank, bar, stem, saddle and seatpost sizes
// come from the bill lines (BikeSpec). These are drawing positions only.

export type P2 = readonly [number, number]

const DEG = Math.PI / 180

export const add2 = (p: P2, d: P2, k = 1): P2 => [p[0] + d[0] * k, p[1] + d[1] * k]
export const at = (p: P2, z = 0): V3 => [p[0], p[1], z]
export const dist2 = (a: P2, b: P2) => Math.hypot(b[0] - a[0], b[1] - a[1])
export const angle2 = (a: P2, b: P2) => Math.atan2(b[1] - a[1], b[0] - a[0])

/** Pitch radius of a sprocket for a 1/2 inch chain. */
export const pitchRadius = (teeth: number) => 12.7 / (2 * Math.sin(Math.PI / teeth))

export interface HubSpec {
  flangeR: readonly [number, number]
  flangeZ: readonly [number, number]
  axle: number
  discZ: number
}

export function bikeGeometry(spec: BikeSpec) {
  const rimBead = 311
  const tyre = spec.tyreMm
  const wheelR = rimBead + tyre * 0.92
  const axleY = wheelR
  const bb: P2 = [0, axleY - 70]
  const rear: P2 = [-Math.sqrt(425 ** 2 - 70 ** 2), axleY]
  const front: P2 = [rear[0] + 1030, axleY]
  const seatDir: P2 = [-Math.cos(73.5 * DEG), Math.sin(73.5 * DEG)]
  const headDir: P2 = [-Math.cos(71.5 * DEG), Math.sin(71.5 * DEG)]
  const seatTop = add2(bb, seatDir, 540)
  const headTop: P2 = [bb[0] + 378, bb[1] + 592]
  const headBot = add2(headTop, headDir, -160)
  const crown = add2(headBot, headDir, -22)
  const stemStart = add2(headTop, headDir, 52)
  const stemAngle = (90 - 71.5 + 6) * DEG
  const stemDir: P2 = [Math.cos(stemAngle), Math.sin(stemAngle)]
  const bar = add2(stemStart, stemDir, spec.stemMm)
  const postTop = add2(seatTop, seatDir, 190)
  const downStart: P2 = [bb[0] + 12, bb[1] + 25]
  const downEnd = add2(headBot, headDir, 30)
  const crankAngle = -18 * DEG
  const crankLen = spec.crankMm
  const cogIndex = Math.min(spec.cogs.length - 1, 4)
  return {
    rimBead,
    rimOuter: rimBead + 7,
    rimInner: rimBead - 21,
    holeR: rimBead - 17,
    tyre,
    wheelR,
    bb,
    rear,
    front,
    seatDir,
    headDir,
    seatTop,
    headTop,
    headBot,
    crown,
    stemStart,
    stemAngle,
    stemDir,
    bar,
    postTop,
    saddle: add2(postTop, [-12, 47]),
    topStart: add2(seatTop, seatDir, -30),
    topEnd: add2(headTop, headDir, -20),
    downStart,
    downEnd,
    seatCluster: add2(seatTop, seatDir, -42),
    steererTop: add2(headTop, headDir, 75),
    crankLen,
    crankAngle,
    pedalRight: add2(bb, [Math.cos(crankAngle), Math.sin(crankAngle)], crankLen),
    pedalLeft: add2(bb, [Math.cos(crankAngle + Math.PI), Math.sin(crankAngle + Math.PI)], crankLen),
    ringZ: [52, 45] as const,
    cogZ: (i: number) => 61 - i * 3.7,
    cogIndex,
    cogR: pitchRadius(spec.cogs[cogIndex] ?? 18),
    bigRing: pitchRadius(spec.chainrings[0] ?? 50),
    jockeyUpper: add2(rear, [13, -72]),
    jockeyLower: add2(rear, [-12, -142]),
    jockeyR: pitchRadius(11),
    rotorR: spec.rotorMm / 2,
    halfBar: spec.barMm / 2,
    /** hubs: flange radius and z of each flange (non-drive, drive), axle half width, and where the disc sits */
    hubFront: { flangeR: [22, 22], flangeZ: [-35, 35], axle: 50, discZ: -42 } as HubSpec,
    hubRear: { flangeR: [26, 24], flangeZ: [-35, 22], axle: 71, discZ: -48 } as HubSpec,
    rearDropoutZ: 66,
    forkZ: 50,
    chainZ: 50,
  }
}

export type Geometry = ReturnType<typeof bikeGeometry>

// ---------------------------------------------------------------- spokes

export interface SpokeLine {
  /** hub flange and rim hole, in the wheel's frame (axle at the origin) */
  hub: V3
  rim: V3
  side: 0 | 1
}

/** A two-cross lacing: rim holes alternate sides, and each side alternates leading and trailing spokes. */
export function lacing(g: Geometry, count: number, hub: HubSpec): SpokeLine[] {
  const out: SpokeLine[] = []
  const perSide = count / 2
  const cross = Math.min(2, Math.max(0, Math.floor(perSide / 4) - 1))
  const offset = (cross * 2 * Math.PI) / perSide
  for (let i = 0; i < count; i++) {
    const side = (i % 2) as 0 | 1
    const a = (i / count) * Math.PI * 2 + Math.PI / 2
    const lead = Math.floor(i / 2) % 2 === 0 ? 1 : -1
    const h = a + lead * offset
    const r = hub.flangeR[side]
    out.push({
      hub: [Math.cos(h) * r, Math.sin(h) * r, hub.flangeZ[side]],
      rim: [Math.cos(a) * g.holeR, Math.sin(a) * g.holeR, 0],
      side,
    })
  }
  return out
}

// ---------------------------------------------------------------- chain

interface Wheel {
  c: P2
  r: number
  /** +1 the chain runs anticlockwise round it (y up), -1 clockwise */
  s: 1 | -1
}

/** Where a chain leaving circle a meets circle b: the common tangent with each circle on the wrap side. */
function tangent(a: Wheel, b: Wheel): { p: P2; q: P2 } {
  const dx = b.c[0] - a.c[0]
  const dy = b.c[1] - a.c[1]
  const d = Math.hypot(dx, dy)
  const k = (b.s * b.r - a.s * a.r) / d
  const psi = Math.atan2(dy, dx)
  const spread = Math.acos(Math.max(-1, Math.min(1, k)))
  for (const phi of [psi + spread, psi - spread]) {
    const n: P2 = [Math.cos(phi), Math.sin(phi)]
    const dir: P2 = [n[1], -n[0]]
    if (dir[0] * dx + dir[1] * dy > 0) {
      return { p: [a.c[0] - a.s * a.r * n[0], a.c[1] - a.s * a.r * n[1]], q: [b.c[0] - b.s * b.r * n[0], b.c[1] - b.s * b.r * n[1]] }
    }
  }
  return { p: a.c, q: b.c }
}

function arc(w: Wheel, from: P2, to: P2, out: P2[]) {
  const a0 = Math.atan2(from[1] - w.c[1], from[0] - w.c[0])
  let a1 = Math.atan2(to[1] - w.c[1], to[0] - w.c[0])
  if (w.s > 0) while (a1 <= a0) a1 += Math.PI * 2
  else while (a1 >= a0) a1 -= Math.PI * 2
  const n = Math.max(2, Math.ceil(Math.abs(a1 - a0) * w.r / 6))
  for (let i = 0; i <= n; i++) {
    const t = a0 + ((a1 - a0) * i) / n
    out.push([w.c[0] + Math.cos(t) * w.r, w.c[1] + Math.sin(t) * w.r])
  }
}

/** The chain as a closed polyline (bike frame, y up): big ring, cog, then the S through the derailleur's jockey wheels. */
export function chainLoop(g: Geometry): P2[] {
  const wheels: Wheel[] = [
    { c: g.bb, r: g.bigRing, s: 1 },
    { c: g.rear, r: g.cogR, s: 1 },
    { c: g.jockeyUpper, r: g.jockeyR, s: -1 },
    { c: g.jockeyLower, r: g.jockeyR, s: 1 },
  ]
  const links = wheels.map((w, i) => tangent(w, wheels[(i + 1) % wheels.length]))
  const out: P2[] = []
  wheels.forEach((w, i) => {
    const arrive = links[(i + wheels.length - 1) % wheels.length].q
    const leave = links[i].p
    arc(w, arrive, leave, out)
  })
  out.push(out[0])
  return out
}

export function polylineLength(points: readonly P2[]): number {
  let l = 0
  for (let i = 1; i < points.length; i++) l += dist2(points[i - 1], points[i])
  return l
}

// ---------------------------------------------------------------- outlines

/** A sprocket outline (SVG path, y down) around the origin. */
export function sprocketPath(teeth: number, holeR = 0): string {
  const r = pitchRadius(teeth)
  const tip = r + 3.4
  const root = r - 4.2
  const w = (Math.PI * 2) / teeth
  const pts: string[] = []
  for (let i = 0; i < teeth; i++) {
    const a = i * w
    for (const [rad, ang] of [
      [root, a - w * 0.5],
      [root + 1.2, a - w * 0.3],
      [tip, a - w * 0.12],
      [tip, a + w * 0.12],
      [root + 1.2, a + w * 0.3],
    ] as const) {
      pts.push(`${(Math.cos(ang) * rad).toFixed(2)},${(-Math.sin(ang) * rad).toFixed(2)}`)
    }
  }
  let d = `M${pts.join('L')}Z`
  if (holeR > 0) d += circlePath(holeR)
  return d
}

/** A circle as a path (for even-odd cut-outs). */
export function circlePath(r: number, cx = 0, cy = 0): string {
  return `M${(cx + r).toFixed(2)},${cy.toFixed(2)}A${r},${r} 0 1 0 ${(cx - r).toFixed(2)},${cy.toFixed(2)}A${r},${r} 0 1 0 ${(cx + r).toFixed(2)},${cy.toFixed(2)}Z`
}

/** An annulus path (even-odd). */
export function ringPath(outer: number, inner: number): string {
  return circlePath(outer) + circlePath(inner)
}

/** A polyline as an SVG path in drawing coordinates (y down). */
export function pathOf(points: readonly P2[], closed = false): string {
  if (points.length === 0) return ''
  const d = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p[0].toFixed(1)},${(-p[1]).toFixed(1)}`).join('')
  return closed ? `${d}Z` : d
}
