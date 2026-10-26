import { add, I3, norm, pose, rotZ, type V3 } from '../projection'
import { add2, at, type Geometry, type P2 } from '../geometry'
import { DEG, diameterIn, flat, grid, row, seatAlong, seatAt, threadIn, text, type AssemblyKind, type Ctx, type ItemRecipe, type PieceSpec } from './types'

// The frame family: tube stock cut into the frame's tubes, the welds the filler wire makes, the shielding gas, the swarf the
// cutting leaves, paint, decals, and the small parts pressed or bolted into the frame.

interface Member {
  key: string
  name: string
  a: V3
  b: V3
  d: number
}

const tubeLength = (m: Member) => Math.hypot(m.b[0] - m.a[0], m.b[1] - m.a[1], m.b[2] - m.a[2])

/** The tubes a subassembly is welded from, in bike coordinates. */
export function membersOf(owner: AssemblyKind, g: Geometry, d: number): Member[] {
  const stays = (kind: 'chain' | 'seat'): Member[] =>
    ([-1, 1] as const).map((s) =>
      kind === 'chain'
        ? { key: `chainstay${s}`, name: s > 0 ? 'Drive side chainstay' : 'Chainstay', a: at(add2(g.bb, [-20, 2]), 38 * s), b: at(add2(g.rear, [12, -4]), 62 * s), d }
        : { key: `seatstay${s}`, name: s > 0 ? 'Drive side seatstay' : 'Seatstay', a: at(g.seatCluster, 16 * s), b: at(add2(g.rear, [8, 16]), 60 * s), d: d * 0.86 },
    )
  switch (owner) {
    case 'frontTriangle':
      return [
        { key: 'down', name: 'Down tube', a: at(g.downStart), b: at(g.downEnd), d },
        { key: 'top', name: 'Top tube', a: at(g.topStart), b: at(g.topEnd), d },
        { key: 'seat', name: 'Seat tube', a: at(add2(g.bb, g.seatDir, 8)), b: at(g.seatTop), d },
        { key: 'head', name: 'Head tube', a: at(add2(g.headBot, g.headDir, -4)), b: at(add2(g.headTop, g.headDir, 4)), d: Math.max(d, 50) },
      ]
    case 'chainstays':
      return stays('chain')
    case 'seatstays':
      return stays('seat')
    case 'rearTriangle':
      return [...stays('chain'), ...stays('seat')]
    case 'fork':
      return [
        ...([-1, 1] as const).map((s) => ({
          key: `leg${s}`,
          name: s > 0 ? 'Drive side fork leg' : 'Fork leg',
          a: at(add2(g.crown, [8, 0]), 40 * s),
          b: at(add2(g.front, [-6, 6]), 50 * s),
          d: d * 0.95,
        })),
        { key: 'steerer', name: 'Steerer', a: at(g.crown), b: at(g.steererTop), d: 28.6 },
      ]
    default:
      return []
  }
}

/** Where the filler wire lays its beads, per subassembly. */
export function jointsOf(owner: AssemblyKind, g: Geometry): V3[] {
  const both = (p: P2, z: number): V3[] => [at(p, -z), at(p, z)]
  switch (owner) {
    case 'frontTriangle':
      return [at(g.topEnd), at(g.downEnd), at(g.topStart), at(add2(g.bb, g.seatDir, 22)), at(g.downStart)]
    case 'chainstays':
      return [at(add2(g.bb, [-130, 12]))]
    case 'seatstays':
      return [at(add2(g.seatCluster, [-34, -60]))]
    case 'rearTriangle':
      return [...both(add2(g.rear, [12, -4]), 62), ...both(add2(g.rear, [8, 16]), 60)]
    case 'frame':
      return [...both(add2(g.bb, [-20, 2]), 38), ...both(g.seatCluster, 16)]
    case 'fork':
      return [...both(add2(g.crown, [8, 0]), 40), ...both(add2(g.front, [-6, 6]), 50)]
    default:
      return []
  }
}

/** Raw tube in metres, cut into the members it becomes: the cut pieces lie end to end as the length the bill asks for. */
export function stock(c: Ctx): ItemRecipe {
  const d = diameterIn(c.line.description, 30)
  const all = membersOf(c.owner, c.g, d)
  const mine =
    c.owner === 'frontTriangle' && c.stockCount > 1
      ? c.stockIndex === 0
        ? all.slice(0, 1)
        : all.slice(1).filter((_, i) => i % (c.stockCount - 1) === c.stockIndex - 1)
      : c.stockIndex === 0
        ? all
        : []
  const total = Math.max(120, c.line.usage * 1000)
  const pieces: PieceSpec[] = []
  let x = 0
  const h = Math.max(d, ...mine.map((m) => m.d))
  mine.forEach((m, i) => {
    const length = tubeLength(m)
    pieces.push({
      key: m.key,
      art: 'tube',
      params: { length, diameter: m.d, finish: 'alloy', text: m.name },
      lie: { x, y: h / 2 },
      seat: seatAlong(m.a, m.b),
      behavior: 'fly',
      layer: (m.a[2] + m.b[2]) / 2 + (m.key === 'head' ? 1 : 0),
      coat: true,
      extent: flat(0, -m.d / 2, length, m.d / 2),
      order: i / Math.max(1, mine.length),
    })
    x += length + 8
  })
  const rest = total - x
  if (mine.length === 0 || rest > 30) {
    const length = mine.length === 0 ? total : rest
    pieces.push({
      key: 'offcut',
      art: 'tube',
      params: { length, diameter: d, finish: 'alloy', text: mine.length === 0 ? '' : 'offcut' },
      lie: { x, y: h / 2 },
      behavior: mine.length === 0 ? 'absorb' : 'use',
      layer: 0,
      extent: flat(0, -d / 2, length, d / 2),
    })
    x += length
  }
  return { pieces, size: [Math.max(x, 120), h] }
}

/** TIG filler wire: the spool stays on the bench, the beads appear at the joints of the subassembly it welds. */
export function fillerWire(c: Ctx): ItemRecipe {
  const joints = jointsOf(c.owner, c.g)
  return {
    size: [190, 190],
    pieces: [
      { key: 'spool', art: 'spool', params: { r: 95, text: 'TIG 4043' }, lie: { x: 95, y: 95 }, behavior: 'stay', layer: 0, extent: flat(-95, -95, 95, 95, 0, 55) },
      ...joints.map<PieceSpec>((p, i) => ({
        key: `bead${i}`,
        art: 'bead',
        params: { r: 8 },
        lie: { x: 95, y: 95 },
        seat: pose(p),
        behavior: 'appear',
        layer: p[2] + 2,
        coat: true,
        extent: flat(-9, -9, 9, 9),
        order: i / Math.max(1, joints.length),
      })),
    ],
  }
}

export function gasCylinder(c: Ctx): ItemRecipe {
  const label = /ARGON/.test(text(c.line)) ? 'ARGON' : 'GAS'
  return {
    size: [1380, 230],
    pieces: [{ key: 'cylinder', art: 'cylinder', params: { length: 1380, diameter: 220, text: label }, lie: { x: 0, y: 115 }, behavior: 'stay', layer: 0, extent: flat(0, -115, 1380, 115) }],
  }
}

export function swarf(): ItemRecipe {
  return { size: [190, 110], pieces: [{ key: 'swarf', art: 'swarf', lie: { x: 0, y: 0 }, behavior: 'emit', layer: 0, extent: flat(0, 0, 190, 110) }] }
}

/** A tin of primer or paint; the coat it gives the subassembly is an effect of the build (model.ts). */
export function paintTin(c: Ctx, primer: boolean, colour: string): ItemRecipe {
  const big = /5\s*L|5 LITRE/i.test(text(c.line))
  const r = big ? 85 : 58
  const h = big ? 190 : 120
  return {
    size: [2 * r + h * 0.3 + 10, 2 * r + h * 0.2 + 10],
    pieces: [
      {
        key: 'tin',
        art: 'tin',
        params: { r, h, color: colour, text: primer ? 'PRIMER' : 'PAINT 2K', sub: primer ? 'Etch PR10' : c.spec.paintName },
        lie: { x: r + h * 0.3 + 4, y: r + 4 },
        behavior: 'stay',
        layer: 0,
        extent: flat(-r, -r, r, r, 0, h),
      },
    ],
  }
}

/** The decal sheet: the decals peel off it onto the frame; the backing paper stays. */
export function decals(c: Ctx): ItemRecipe {
  const g = c.g
  const down = norm([g.downEnd[0] - g.downStart[0], g.downEnd[1] - g.downStart[1], 0])
  const downAngle = Math.atan2(down[1], down[0])
  const seatAngle = Math.atan2(g.seatDir[1], g.seatDir[0])
  const brand = c.spec.brand.toUpperCase()
  const word = Math.min(330, 40 + brand.length * 27)
  const pieces: PieceSpec[] = [
    { key: 'sheet', art: 'decalSheet', params: { w: 380, h: 200, text: brand }, lie: { x: 0, y: 0 }, behavior: 'stay', layer: 0, extent: flat(0, 0, 380, 200) },
    {
      key: 'down',
      art: 'decal',
      params: { which: 'word', text: brand, w: word, h: 22 },
      lie: { x: 24, y: 40 },
      seat: pose(add(at(add2(g.downStart, [down[0], down[1]], 150)), [0, 0, 18]), rotZ(downAngle)),
      behavior: 'fly',
      layer: 30,
      extent: flat(0, -11, word, 11),
      order: 0,
    },
    {
      key: 'seat',
      art: 'decal',
      params: { which: 'badge', text: brand.slice(0, 1), w: 44, h: 44 },
      lie: { x: 60, y: 130 },
      seat: pose(add(at(add2(g.bb, g.seatDir, 300)), [0, 0, 17]), rotZ(seatAngle - Math.PI / 2)),
      behavior: 'fly',
      layer: 30,
      extent: flat(-22, -22, 22, 22),
      order: 0.35,
    },
    {
      key: 'head',
      art: 'decal',
      params: { which: 'badge', text: brand.slice(0, 1), w: 34, h: 34 },
      lie: { x: 150, y: 130 },
      seat: pose(add(at(add2(g.headBot, g.headDir, 80)), [0, 0, 26]), rotZ(Math.atan2(g.headDir[1], g.headDir[0]) - Math.PI / 2)),
      behavior: 'fly',
      layer: 30,
      extent: flat(-17, -17, 17, 17),
      order: 0.6,
    },
    {
      key: 'top',
      art: 'decal',
      params: { which: 'stripe', text: c.spec.paintName.toUpperCase(), w: 150, h: 10 },
      lie: { x: 210, y: 140 },
      seat: pose(add(at(add2(g.topStart, [g.topEnd[0] - g.topStart[0], g.topEnd[1] - g.topStart[1]], 0.35)), [0, 0, 17]), rotZ(Math.atan2(g.topEnd[1] - g.topStart[1], g.topEnd[0] - g.topStart[0]))),
      behavior: 'fly',
      layer: 30,
      extent: flat(0, -5, 150, 5),
      order: 0.8,
    },
  ]
  return { size: [380, 200], pieces }
}

/** An integrated headset: the upper and lower bearings, pressed into the ends of the head tube. */
export function headset(c: Ctx): ItemRecipe {
  const g = c.g
  const axis = norm([g.headDir[0], g.headDir[1], 0])
  const across: V3 = [axis[1], -axis[0], 0]
  // the bearing's ring lies square to the head tube: local z along the steering axis
  const r = [across[0], 0, axis[0], across[1], 0, axis[1], 0, -1, 0] as const
  const sizes = [/IS42/.test(text(c.line)) ? 21 : 22, /IS52/.test(text(c.line)) ? 26 : 25]
  return {
    size: [150, 70],
    pieces: sizes.map<PieceSpec>((outer, i) => ({
      key: i === 0 ? 'upper' : 'lower',
      art: 'headsetBearing',
      params: { outer, inner: 15 },
      lie: { x: 32 + i * 80, y: 34 },
      seat: pose(at(i === 0 ? add2(g.headTop, g.headDir, -6) : add2(g.headBot, g.headDir, 6)), r),
      behavior: 'fly',
      approach: [axis[0] * (i === 0 ? 120 : -120), axis[1] * (i === 0 ? 120 : -120), 0],
      explode: [axis[0] * (i === 0 ? 110 : -110), axis[1] * (i === 0 ? 110 : -110), 0],
      layer: 4,
      turns: true,
      extent: flat(-outer, -outer, outer, outer, -4, 4),
      order: i / 2,
    })),
  }
}

export function bbShell(c: Ctx): ItemRecipe {
  return {
    size: [90, 80],
    pieces: [
      {
        key: 'shell',
        art: 'bbShell',
        params: { outer: 21, inner: 17.5, depth: 68 },
        lie: { x: 44, y: 26 },
        seat: seatAt(c.g.bb, -34),
        behavior: 'fly',
        approach: [0, 0, -140],
        layer: 3,
        coat: true,
        extent: flat(-21, -21, 21, 21, 0, 68),
      },
    ],
  }
}

export function bearings(c: Ctx): ItemRecipe {
  const n = Math.max(1, c.count)
  return {
    size: [n * 64, 64],
    pieces: row(n, 64, 32, 32).map<PieceSpec>((p, i) => ({
      key: `bearing${i}`,
      art: 'bearing',
      params: { outer: 13, inner: 8.5 },
      lie: p,
      seat: seatAt(c.g.bb, i % 2 === 0 ? 30 : -30),
      behavior: 'fly',
      approach: [0, 0, i % 2 === 0 ? 120 : -120],
      explode: [0, 0, i % 2 === 0 ? 90 : -90],
      layer: i % 2 === 0 ? 36 : -30,
      extent: flat(-13, -13, 13, 13, 0, 5),
      order: i / n,
    })),
  }
}

export function dropout(c: Ctx): ItemRecipe {
  const right = /\bRH\b|-RH|RIGHT/.test(text(c.line))
  const s = right ? 1 : -1
  return {
    size: [120, 120],
    pieces: [
      {
        key: 'dropout',
        art: 'dropout',
        params: { side: s },
        lie: { x: 50, y: 52 },
        seat: seatAt(c.g.rear, 66 * s),
        behavior: 'fly',
        approach: [0, 0, 110 * s],
        explode: [0, 0, 70 * s],
        layer: 66 * s,
        extent: flat(-45, -45, 60, 60),
      },
    ],
  }
}

export function bracket(c: Ctx): ItemRecipe {
  const g = c.g
  const fork = c.owner === 'fork'
  const at0: P2 = fork ? add2(g.front, [-34, 64]) : add2(g.rear, [70, 24])
  const angle = fork ? Math.atan2(g.headDir[1], g.headDir[0]) + Math.PI : -8 * DEG
  return {
    size: [80, 50],
    pieces: [
      {
        key: 'bracket',
        art: 'bracket',
        lie: { x: 40, y: 25 },
        seat: pose(at(at0, fork ? -52 : -46), rotZ(angle)),
        behavior: 'fly',
        approach: [0, 0, -90],
        layer: fork ? -52 : -46,
        coat: true,
        extent: flat(-30, -16, 30, 16),
      },
    ],
  }
}

// ---------------------------------------------------------------- small hardware

export interface HardPoint {
  p: V3
  /** pointing into the part it is fitted to */
  axis: V3
}

/** The threaded bosses of a frame: two on the down tube, two on the seat tube, a rack mount on each seatstay. */
export function bosses(g: Geometry): HardPoint[] {
  const down = norm([g.downEnd[0] - g.downStart[0], g.downEnd[1] - g.downStart[1], 0])
  const up: V3 = [-down[1], down[0], 0]
  const seatFront: V3 = [g.seatDir[1], -g.seatDir[0], 0]
  const len = Math.hypot(g.downEnd[0] - g.downStart[0], g.downEnd[1] - g.downStart[1])
  const onDown = (t: number): HardPoint => ({ p: add(add(at(g.downStart), down, len * t), up, 17), axis: [-up[0], -up[1], 0] })
  const onSeat = (t: number): HardPoint => ({ p: add(at(add2(g.bb, g.seatDir, 540 * t)), seatFront, 16), axis: [-seatFront[0], -seatFront[1], 0] })
  const stay = add2(g.seatCluster, [g.rear[0] - g.seatCluster[0], g.rear[1] - g.seatCluster[1]], 0.18)
  return [onDown(0.3), onDown(0.48), onSeat(0.3), onSeat(0.47), { p: at(stay, 30), axis: [0, 0, -1] }, { p: at(stay, -30), axis: [0, 0, 1] }]
}

/** Points for loose hardware by what it is: rack and kickstand bolts, the dropout plate bolts, the headset's cover screws. */
export function hardPoints(c: Ctx, kind: 'boss' | 'm5' | 'm6' | 'washer' | 'nut' | 'screw' | 'dropoutBolt'): HardPoint[] {
  const g = c.g
  const b = bosses(g)
  const out = (p: P2, z: number): HardPoint => ({ p: at(p, z), axis: [0, 0, z > 0 ? -1 : 1] })
  const headFront: V3 = [g.headDir[1], -g.headDir[0], 0]
  switch (kind) {
    case 'boss':
      return b
    case 'm5':
      return [b[0], b[1], b[4], b[5]].map((h) => ({ p: add(h.p, h.axis, -3), axis: h.axis }))
    case 'm6':
      return [out(add2(g.rear, [-28, -24]), 80), out(add2(g.rear, [-28, -24]), -80)]
    case 'washer':
      return [out(add2(g.rear, [-28, -24]), 78), out(add2(g.rear, [-28, -24]), -78), out(add2(g.rear, [-28, -24]), 60), out(add2(g.rear, [-28, -24]), -60)]
    case 'nut':
      return [out(add2(g.rear, [-28, -24]), 58), out(add2(g.rear, [-28, -24]), -58)]
    case 'dropoutBolt':
      return [out(add2(g.rear, [-20, -22]), 72), out(add2(g.rear, [-20, -22]), -72)]
    case 'screw':
      return [0.25, 0.7].map((t) => ({ p: add(at(add2(g.headTop, g.headDir, -160 * t)), headFront, 26), axis: [-headFront[0], -headFront[1], 0] }))
  }
}

/** Bolts, screws, inserts, washers and nuts, laid out in a row and fitted at their points. */
export function fasteners(c: Ctx, kind: 'bolt' | 'screw' | 'insert' | 'washer' | 'nut'): ItemRecipe {
  const t = text(c.line)
  const [m, l] = threadIn(c.line.description, kind === 'screw' ? [4, 10] : [5, 16])
  const titanium = /\bTI\b|TITANIUM/.test(t)
  const points =
    kind === 'insert'
      ? hardPoints(c, 'boss')
      : kind === 'washer'
        ? hardPoints(c, 'washer')
        : kind === 'nut'
          ? hardPoints(c, 'nut')
          : kind === 'screw'
            ? hardPoints(c, 'screw')
            : c.owner === 'dropouts'
              ? hardPoints(c, 'dropoutBolt')
              : m >= 6
                ? hardPoints(c, 'm6')
                : hardPoints(c, 'm5')
  const n = Math.max(1, c.count)
  const pitch = kind === 'washer' || kind === 'nut' ? m * 2.6 : l + m * 2 + 10
  const across = kind === 'washer' || kind === 'nut' ? m * 2.6 : m * 2 + 10
  const cols = Math.min(n, kind === 'washer' || kind === 'nut' ? 6 : 3)
  const spots = grid(n, cols, pitch, across, kind === 'washer' || kind === 'nut' ? m * 1.3 : 2, across / 2)
  const pieces = spots.map<PieceSpec>((spot, i) => {
    const hp = points[i % points.length]
    const shift = Math.floor(i / points.length) * 12
    const seatPose = kind === 'washer' || kind === 'nut' ? pose(add(hp.p, [0, shift, 0]), I3) : seatAlong(add(hp.p, [0, shift, 0]), add(add(hp.p, [0, shift, 0]), hp.axis))
    return {
      key: `${kind}${i}`,
      art: kind === 'washer' ? 'washer' : kind === 'nut' ? 'nut' : 'bolt',
      params:
        kind === 'washer' || kind === 'nut'
          ? { r: m * 1.05, inner: m * 0.55 }
          : kind === 'insert'
            ? { head: [m * 1.8, 1.2], shank: [m * 1.4, 11], finish: 'alloy', which: 'insert' }
            : { head: [kind === 'screw' ? m * 1.9 : m * 1.6, kind === 'screw' ? m * 0.55 : m], shank: [m, l], finish: titanium ? 'titanium' : 'steel', which: kind === 'screw' ? 'button' : 'socket' },
      lie: spot,
      seat: seatPose,
      behavior: 'fly',
      approach: kind === 'washer' || kind === 'nut' ? [0, 0, hp.p[2] > 0 ? 60 : -60] : [-hp.axis[0] * 40, -hp.axis[1] * 40, -hp.axis[2] * 40],
      explode: [-hp.axis[0] * 70, -hp.axis[1] * 70, -hp.axis[2] * 70],
      layer: hp.p[2] + 20,
      turns: kind !== 'washer' && kind !== 'nut',
      extent: kind === 'washer' || kind === 'nut' ? flat(-m * 1.3, -m * 1.3, m * 1.3, m * 1.3) : flat(0, -m, l + m, m),
      order: i / n,
    }
  })
  const w = kind === 'washer' || kind === 'nut' ? cols * pitch : cols * pitch
  return { pieces, size: [w, Math.ceil(n / cols) * across] }
}
