import { add, pose, rotX, rotZ, type V3 } from '../projection'
import { add2, at, chainLoop, dist2, type Geometry, type P2 } from '../geometry'
import { DEG, flat, LIE_SIDEWAYS, seatAlong, seatAt, text, type Ctx, type ItemRecipe, type PieceSpec } from './types'

// Cockpit, drivetrain, brakes and cables, saddle and seatpost.

// ---------------------------------------------------------------- cockpit

/** The cockpit lies on its back in its cell: bar across the mat, stem pointing up the mat. */
export const COCKPIT_LYING = [0, 0, 1, 1, 0, 0, 0, 1, 0] as const

export function handlebar(c: Ctx): ItemRecipe {
  const half = c.g.halfBar
  return {
    size: [half * 2 + 20, 60],
    pieces: [
      {
        key: 'bar',
        art: 'handlebar',
        params: { length: half * 2, diameter: 22.2, outer: 31.8 },
        lie: { x: half + 10, y: 30, r: LIE_SIDEWAYS },
        seat: seatAt(c.g.bar),
        behavior: 'fly',
        approach: [0, 0, 260],
        layer: 46,
        turns: true,
        extent: [
          [-16, -16, -half],
          [16, 16, half],
        ],
      },
    ],
  }
}

export function stem(c: Ctx): ItemRecipe {
  const g = c.g
  const L = c.spec.stemMm
  return {
    size: [L + 70, 110],
    pieces: [
      {
        key: 'stem',
        art: 'stem',
        params: { length: L, r: g.stemAngle, outer: Math.atan2(g.headDir[1], g.headDir[0]) },
        lie: { x: 30, y: 72 },
        seat: seatAt(g.stemStart),
        behavior: 'fly',
        approach: [g.headDir[0] * 120, g.headDir[1] * 120, 0],
        layer: 8,
        turns: true,
        extent: [
          [-24, -26, -28],
          [L * Math.cos(g.stemAngle) + 22, L * Math.sin(g.stemAngle) + 26, 28],
        ],
      },
    ],
  }
}

export function grips(c: Ctx): ItemRecipe {
  const half = c.g.halfBar
  return {
    size: [320, 70],
    pieces: ([-1, 1] as const).map<PieceSpec>((s, i) => ({
      key: s > 0 ? 'right' : 'left',
      art: 'grip',
      params: { side: s, length: 130, r: half },
      lie: { x: 20 + i * 150 - (s > 0 ? half - 130 : -half), y: 35, r: LIE_SIDEWAYS },
      seat: seatAt(c.g.bar),
      behavior: 'fly',
      approach: [0, 0, 150 * s],
      layer: s > 0 ? 60 : -60,
      turns: true,
      extent: [
        [-17, -17, s > 0 ? half - 130 : -half],
        [17, 17, s > 0 ? half : -(half - 130)],
      ],
      order: i / 2,
    })),
  }
}

export function shifter(c: Ctx): ItemRecipe {
  const half = c.g.halfBar
  return {
    size: [150, 90],
    pieces: [
      {
        key: 'shifter',
        art: 'shifter',
        params: { r: half },
        lie: { x: 60 - (half - 150), y: 40, r: LIE_SIDEWAYS },
        seat: seatAt(c.g.bar),
        behavior: 'fly',
        approach: [0, 0, 120],
        layer: 50,
        turns: true,
        extent: [
          [-40, -50, half - 190],
          [20, 10, half - 130],
        ],
      },
    ],
  }
}

export function bell(c: Ctx): ItemRecipe {
  const z = -(c.g.halfBar - 165)
  return {
    size: [90, 80],
    pieces: [
      {
        key: 'bell',
        art: 'bell',
        lie: { x: 45, y: 46, r: rotX(Math.PI / 2) },
        seat: seatAt(c.g.bar, z),
        behavior: 'fly',
        approach: [0, 90, 0],
        explode: [0, 110, -60],
        layer: z + 30,
        turns: true,
        extent: [
          [-28, 0, -28],
          [28, 42, 28],
        ],
      },
    ],
  }
}

// ---------------------------------------------------------------- drivetrain

export function crankset(c: Ctx): ItemRecipe {
  const g = c.g
  const reach = g.crankLen + 20
  return {
    size: [reach * 2 + 70, 2 * g.bigRing + 70],
    pieces: [
      {
        key: 'crankset',
        art: 'crankset',
        params: { teeth: c.spec.chainrings, length: g.crankLen, r: g.crankAngle, zs: [g.ringZ[0], g.ringZ[1]] },
        lie: { x: reach + 54, y: g.bigRing + 18, z: 80 },
        seat: seatAt(g.bb),
        behavior: 'fly',
        approach: [0, 0, 260],
        explode: [0, -60, 170],
        layer: 70,
        extent: [
          [-reach, -reach, -86],
          [reach, reach, 86],
        ],
      },
    ],
  }
}

export function chain(c: Ctx): ItemRecipe {
  const g = c.g
  const loop = chainLoop(g)
  const xs = loop.map((p) => p[0])
  const ys = loop.map((p) => p[1])
  const minX = Math.min(...xs)
  const maxX = Math.max(...xs)
  const minY = Math.min(...ys)
  const maxY = Math.max(...ys)
  return {
    size: [maxX - minX + 12, maxY - minY + 12],
    pieces: [
      {
        key: 'chain',
        art: 'chain',
        params: { points: loop, holes: c.spec.chainLinks },
        lie: { x: -minX + 6, y: maxY + 6 },
        seat: pose([0, 0, g.chainZ]),
        behavior: 'fly',
        approach: [0, 0, 120],
        explode: [0, -170, 110],
        layer: 55,
        extent: [
          [minX - 5, minY - 5, 0],
          [maxX + 5, maxY + 5, 0],
        ],
      },
    ],
  }
}

/** The lower run of the chain: its longest straight below the bottom bracket. */
function lowerRun(g: Geometry): { p: P2; angle: number } {
  const loop = chainLoop(g)
  let best = { p: g.bb as P2, angle: 0, l: 0 }
  for (let i = 1; i < loop.length; i++) {
    const a = loop[i - 1]
    const b = loop[i]
    const l = dist2(a, b)
    if (l > best.l && (a[1] + b[1]) / 2 < g.bb[1] - 20) best = { p: [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2], angle: Math.atan2(b[1] - a[1], b[0] - a[0]), l }
  }
  return best
}

export function quickLink(c: Ctx): ItemRecipe {
  const run = lowerRun(c.g)
  return {
    size: [60, 40],
    pieces: [
      {
        key: 'link',
        art: 'quickLink',
        lie: { x: 30, y: 20 },
        seat: pose([run.p[0], run.p[1], c.g.chainZ + 2], rotZ(run.angle)),
        behavior: 'fly',
        approach: [0, 0, 80],
        explode: [0, -170, 110],
        layer: 57,
        extent: flat(-13, -6, 13, 6),
      },
    ],
  }
}

export function derailleur(c: Ctx): ItemRecipe {
  const g = c.g
  const up: P2 = [g.jockeyUpper[0] - g.rear[0], g.jockeyUpper[1] - g.rear[1]]
  const low: P2 = [g.jockeyLower[0] - g.rear[0], g.jockeyLower[1] - g.rear[1]]
  return {
    size: [150, 200],
    pieces: [
      {
        key: 'derailleur',
        art: 'derailleur',
        params: { points: [up, low], r: g.jockeyR },
        lie: { x: 70, y: 22 },
        seat: seatAt(g.rear),
        behavior: 'fly',
        approach: [0, 0, 120],
        explode: [-90, -110, 90],
        layer: 62,
        extent: [
          [-60, low[1] - 30, 50],
          [60, 30, 70],
        ],
      },
    ],
  }
}

export function pedals(c: Ctx): ItemRecipe {
  const g = c.g
  return {
    size: [260, 170],
    pieces: ([1, -1] as const).map<PieceSpec>((s, i) => ({
      key: s > 0 ? 'right' : 'left',
      art: 'pedal',
      params: { side: s },
      lie: { x: 65 + i * 130, y: s > 0 ? 14 : 156, r: rotX(Math.PI / 2) },
      seat: seatAt(s > 0 ? g.pedalRight : g.pedalLeft, 80 * s),
      behavior: 'fly',
      approach: [0, 0, 150 * s],
      explode: [0, -40, 230 * s],
      layer: s > 0 ? 120 : -120,
      turns: true,
      extent: [
        [-52, -14, s > 0 ? 0 : -140],
        [52, 14, s > 0 ? 140 : 0],
      ],
      order: i / 2,
    })),
  }
}

// ---------------------------------------------------------------- brakes and cables

/** Where each caliper grips its rotor: the front one behind the fork leg, the rear one on the chainstay. */
export function caliperSeat(g: Geometry, front: boolean) {
  const axle = front ? g.front : g.rear
  const a = (front ? 128 : 52) * DEG
  return pose([axle[0] + Math.cos(a) * (g.rotorR - 8), axle[1] + Math.sin(a) * (g.rotorR - 8), -58], rotZ(a - Math.PI / 2))
}

export function calipers(c: Ctx): ItemRecipe {
  const n = Math.max(1, Math.min(2, c.count))
  return {
    size: [n * 110, 70],
    pieces: [...Array(n).keys()].map<PieceSpec>((i) => ({
      key: i === 0 ? 'front' : 'rear',
      art: 'caliper',
      lie: { x: 55 + i * 110, y: 38 },
      seat: caliperSeat(c.g, i === 0),
      behavior: 'fly',
      approach: [0, 0, -110],
      explode: [0, 0, -110],
      layer: -58,
      extent: flat(-38, -26, 38, 22),
      order: i / n,
    })),
  }
}

type RouteKind = 'hose' | 'housing' | 'cable'

/** Routes through the frame (bike coordinates): the internal stretch is drawn too, hidden behind the tubes it runs in. */
function routesOf(g: Geometry, kind: RouteKind): V3[][] {
  const B = g.bar
  const half = g.halfBar
  const down: P2 = [g.downEnd[0] - g.downStart[0], g.downEnd[1] - g.downStart[1]]
  const dl = Math.hypot(down[0], down[1])
  const port = add2(g.downEnd, down, -90 / dl)
  const alongDown = (t: number, z: number): V3 => at(add2(g.downStart, down, t), z)
  const derailleurClamp = add2(g.rear, [-8, -36])
  if (kind === 'hose') {
    const front: V3[] = [
      at(add2(B, [-6, -16]), -(half - 160)),
      at(add2(B, [36, -110]), -90),
      at(add2(g.crown, [44, -20]), -54),
      at(add2(g.front, [-26, 130]), -54),
      add(caliperSeat(g, true).t, [6, 10, 0]),
    ]
    const rear: V3[] = [
      at(add2(B, [-14, -16]), -(half - 175)),
      at(add2(B, [-30, -150]), -60),
      at(add2(port, [-6, 6]), -8),
      alongDown(0.5, -6),
      alongDown(0.08, -10),
      at(add2(g.bb, [-60, -6]), -30),
      at(add2(g.rear, [110, 10]), -48),
      add(caliperSeat(g, false).t, [-8, 6, 0]),
    ]
    return [front, rear]
  }
  const z = kind === 'cable' ? 1 : 0
  return [
    [
      at(add2(B, [-4, -28]), half - 150 + z),
      at(add2(B, [44, -150]), 70),
      at(add2(port, [4, 10]), 10),
      alongDown(0.5, 6 + z),
      alongDown(0.08, 8 + z),
      at(add2(g.bb, [-56, -10]), 34),
      at(add2(g.rear, [96, 6]), 50),
      at(add2(g.rear, [40, -8]), 62),
      at(derailleurClamp, 64 + z),
    ],
  ]
}

const ROUTE_STYLE: Record<RouteKind, { width: number; color: string }> = {
  hose: { width: 5, color: '#0b0c0e' },
  housing: { width: 4.2, color: '#16181c' },
  cable: { width: 1.2, color: '#d6dae0' },
}

/** Cable, housing or hose: the coil is used up as the run draws itself along the bike. */
export function cableRun(c: Ctx): ItemRecipe {
  const t = text(c.line)
  const kind: RouteKind = /HOSE/.test(t) ? 'hose' : /HOUSING/.test(t) ? 'housing' : 'cable'
  const style = ROUTE_STYLE[kind]
  const routes = routesOf(c.g, kind)
  const metres = c.line.unit.trim().toUpperCase() === 'M' ? c.line.usage : 0
  const pieces: PieceSpec[] = [
    {
      key: 'coil',
      art: 'coil',
      params: { r: kind === 'cable' ? 70 : 82, color: style.color, w: style.width, text: metres ? `${metres} m` : '', which: kind },
      lie: { x: 95, y: 95 },
      behavior: 'use',
      layer: 0,
      extent: flat(-95, -95, 95, 95),
    },
    ...routes.map<PieceSpec>((points, i) => {
      const xs = points.map((p) => p[0])
      const ys = points.map((p) => p[1])
      return {
        key: `run${i}`,
        art: 'route',
        params: { points: points.map((p) => [p[0], p[1]] as P2), zs: points.map((p) => p[2]), w: style.width, color: style.color, which: kind },
        lie: { x: 95, y: 95 },
        seat: pose([0, 0, 0]),
        behavior: 'appear',
        layer: kind === 'hose' ? -4 : -3,
        extent: [
          [Math.min(...xs), Math.min(...ys), -100],
          [Math.max(...xs), Math.max(...ys), 100],
        ],
        order: i / routes.length,
      }
    }),
  ]
  return { size: [190, 190], pieces }
}

/** Ferrules cap the ends of the gear housing: at the shifter, the down tube port, the chainstay exit and the derailleur. */
export function ferrules(c: Ctx): ItemRecipe {
  const g = c.g
  const n = Math.max(1, c.count)
  const route = routesOf(g, 'housing')[0]
  const ends = [route[0], route[2], route[route.length - 3], route[route.length - 1]]
  return {
    size: [n * 26 + 10, 40],
    pieces: [...Array(n).keys()].map<PieceSpec>((i) => {
      const p = ends[i % ends.length]
      const q = route[Math.min(route.length - 1, Math.max(0, route.indexOf(p) + (i % 2 === 0 ? 1 : -1)))]
      return {
        key: `ferrule${i}`,
        art: 'bolt',
        params: { head: [6, 3], shank: [4.6, 6], finish: 'silver', which: 'ferrule' },
        lie: { x: 8 + i * 26, y: 20 },
        seat: seatAlong(p, q),
        behavior: 'fly',
        layer: p[2] + 4,
        turns: true,
        extent: flat(0, -3, 9, 3),
        order: i / n,
      }
    }),
  }
}

// ---------------------------------------------------------------- saddle and seatpost

export function saddle(c: Ctx): ItemRecipe {
  return {
    size: [300, 110],
    pieces: [
      {
        key: 'saddle',
        art: 'saddle',
        params: { w: c.spec.saddleMm },
        lie: { x: 150, y: 46 },
        seat: seatAt(c.g.saddle),
        behavior: 'fly',
        approach: [0, 120, 0],
        explode: [-20, 230, 0],
        layer: 9,
        extent: [
          [-140, -60, -c.spec.saddleMm / 2],
          [140, 40, c.spec.saddleMm / 2],
        ],
      },
    ],
  }
}

export function seatpost(c: Ctx): ItemRecipe {
  const g = c.g
  const { diameter, length } = c.spec.seatpost
  const top = g.postTop
  const bottom = add2(top, g.seatDir, -length)
  return {
    size: [length + 40, 60],
    pieces: [
      {
        key: 'seatpost',
        art: 'seatpost',
        params: { length, diameter },
        lie: { x: 6, y: 30 },
        seat: seatAlong(at(bottom), at(top)),
        behavior: 'fly',
        approach: [g.seatDir[0] * 240, g.seatDir[1] * 240, 0],
        explode: [g.seatDir[0] * 130, g.seatDir[1] * 130, 0],
        layer: -1,
        turns: true,
        extent: [
          [-4, -diameter / 2 - 10, -diameter / 2],
          [length + 30, diameter / 2 + 10, diameter / 2],
        ],
      },
    ],
  }
}
