import { pose } from '../projection'
import { lacing, type P2 } from '../geometry'
import { flat, grid, LIE_SIDEWAYS, seatAlong, seatAt, text, type Ctx, type ItemRecipe, type PieceSpec } from './types'

// The wheels: rim, hub, spokes and nipples are built into a wheel in its cell; rim tape draws itself round the rim bed, the
// rotor and cassette bolt to the hub, and the tyres and inner tubes go on at final assembly.

const axleOf = (c: Ctx): P2 => (c.owner === 'rearWheel' ? c.g.rear : c.g.front)
const hubOf = (c: Ctx) => (c.owner === 'rearWheel' ? c.g.hubRear : c.g.hubFront)

export function rim(c: Ctx): ItemRecipe {
  const g = c.g
  const tubeless = /TUBELESS/.test(text(c.line))
  return {
    size: [g.rimOuter * 2 + 14, g.rimOuter * 2 + 10],
    pieces: [
      {
        key: 'rim',
        art: 'rim',
        params: { outer: g.rimOuter, inner: g.rimInner, holes: c.spec.spokes, depth: 24, text: tubeless ? 'TUBELESS READY · 700C' : '700C' },
        lie: { x: g.rimOuter + 8, y: g.rimOuter },
        seat: seatAt(axleOf(c), -12),
        behavior: 'fly',
        layer: 1,
        extent: flat(-g.rimOuter, -g.rimOuter, g.rimOuter, g.rimOuter, 0, 24),
      },
    ],
  }
}

export function hub(c: Ctx): ItemRecipe {
  const h = hubOf(c)
  const rear = c.owner === 'rearWheel' || /REAR/.test(text(c.line))
  const r = Math.max(...h.flangeR)
  return {
    size: [h.axle * 2 + 30, r * 2 + 40],
    pieces: [
      {
        key: 'hub',
        art: 'hub',
        params: { which: rear ? 'rear' : 'front', r: h.flangeR[0], outer: h.flangeR[1], zs: [h.flangeZ[0], h.flangeZ[1], h.discZ, h.axle], holes: c.spec.spokes / 2 },
        lie: { x: h.axle + 15, y: r + 20, r: LIE_SIDEWAYS },
        seat: seatAt(axleOf(c)),
        behavior: 'fly',
        approach: [0, 0, 160],
        layer: 12,
        turns: true,
        extent: [
          [-r, -r, -h.axle],
          [r, r, h.axle],
        ],
      },
    ],
  }
}

export function spokes(c: Ctx): ItemRecipe {
  const n = Math.max(2, Math.min(48, c.count || c.spec.spokes))
  const axle = axleOf(c)
  const lines = lacing(c.g, n % 2 === 0 ? n : n + 1, hubOf(c)).slice(0, n)
  const longest = Math.max(...lines.map((s) => Math.hypot(s.rim[0] - s.hub[0], s.rim[1] - s.hub[1], s.rim[2] - s.hub[2])))
  const pitch = 7
  return {
    size: [longest + 8, n * pitch + 4],
    pieces: lines.map<PieceSpec>((s, i) => {
      const a = [axle[0] + s.hub[0], axle[1] + s.hub[1], s.hub[2]] as const
      const b = [axle[0] + s.rim[0], axle[1] + s.rim[1], s.rim[2]] as const
      const length = Math.hypot(b[0] - a[0], b[1] - a[1], b[2] - a[2])
      return {
        key: `spoke${i}`,
        art: 'spoke',
        params: { length },
        lie: { x: 4, y: 4 + i * pitch },
        seat: seatAlong(a, b),
        behavior: 'fly',
        layer: s.side === 0 ? -20 : 18,
        extent: flat(0, -1.2, length, 1.2),
        order: i / n,
      }
    }),
  }
}

export function nipples(c: Ctx): ItemRecipe {
  const n = Math.max(2, Math.min(48, c.count || c.spec.spokes))
  const axle = axleOf(c)
  const lines = lacing(c.g, n % 2 === 0 ? n : n + 1, hubOf(c)).slice(0, n)
  const cols = 8
  return {
    size: [cols * 18, Math.ceil(n / cols) * 10 + 4],
    pieces: grid(n, cols, 18, 10, 14, 7).map<PieceSpec>((spot, i) => {
      const s = lines[i]
      const d = [s.rim[0] - s.hub[0], s.rim[1] - s.hub[1], s.rim[2] - s.hub[2]] as const
      const l = Math.hypot(d[0], d[1], d[2])
      const end = [axle[0] + s.rim[0] - (d[0] / l) * 2, axle[1] + s.rim[1] - (d[1] / l) * 2, s.rim[2] - (d[2] / l) * 2] as const
      return {
        key: `nipple${i}`,
        art: 'nipple',
        lie: spot,
        seat: seatAlong(end, [end[0] + d[0], end[1] + d[1], end[2] + d[2]]),
        behavior: 'fly',
        layer: 20,
        extent: flat(-12, -2, 0, 2),
        order: i / n,
      }
    }),
  }
}

export function rimTape(c: Ctx): ItemRecipe {
  const g = c.g
  const width = c.line.description.match(/(\d+)\s*mm/)?.[1] ?? '25'
  return {
    size: [110, 110],
    pieces: [
      { key: 'roll', art: 'tapeRoll', params: { r: 44, inner: 19, text: `${width} mm` }, lie: { x: 55, y: 55 }, behavior: 'use', layer: 0, extent: flat(-44, -44, 44, 44, 0, Number(width)) },
      { key: 'ring', art: 'tapeRing', params: { r: g.rimBead + 1 }, lie: { x: 55, y: 55 }, seat: seatAt(axleOf(c), 4), behavior: 'appear', layer: 2, extent: flat(-g.rimBead - 3, -g.rimBead - 3, g.rimBead + 3, g.rimBead + 3) },
    ],
  }
}

export function rotor(c: Ctx): ItemRecipe {
  const r = c.g.rotorR
  return {
    size: [r * 2 + 10, r * 2 + 10],
    pieces: [
      {
        key: 'rotor',
        art: 'rotor',
        params: { r },
        lie: { x: r + 5, y: r + 5 },
        seat: seatAt(axleOf(c), hubOf(c).discZ),
        behavior: 'fly',
        approach: [0, 0, -120],
        explode: [0, 0, -130],
        layer: hubOf(c).discZ,
        extent: flat(-r, -r, r, r),
      },
    ],
  }
}

export function cassette(c: Ctx): ItemRecipe {
  const g = c.g
  const cogs = c.spec.cogs
  const big = (12.7 / (2 * Math.sin(Math.PI / Math.max(...cogs)))) + 4
  return {
    size: [big * 2 + 30, big * 2 + 20],
    pieces: [
      {
        key: 'cassette',
        art: 'cassette',
        params: { teeth: cogs, zs: cogs.map((_, i) => g.cogZ(i)) },
        lie: { x: big + 22, y: big + 2, z: -g.cogZ(cogs.length - 1) },
        seat: seatAt(g.rear),
        behavior: 'fly',
        approach: [0, 0, 140],
        explode: [0, 0, 150],
        layer: 40,
        extent: [
          [-big, -big, g.cogZ(cogs.length - 1)],
          [big, big, g.cogZ(0)],
        ],
      },
    ],
  }
}

/** Tyres and inner tubes: they lie folded and open out round as they go on, the first on the front wheel, the next on the rear. */
export function tyres(c: Ctx, inner: boolean): ItemRecipe {
  const g = c.g
  const n = Math.max(1, Math.min(2, c.count))
  const outer = inner ? g.rimBead + 14 : g.wheelR
  const pitch = inner ? 200 : 300
  const size = c.line.description.match(/700\s*x\s*[\d-]+C?/i)?.[0]?.replace(/\s*x\s*/i, ' × ') ?? '700C'
  const folding = /FOLDING/.test(text(c.line))
  return {
    size: [n * pitch, inner ? 120 : 170],
    pieces: [...Array(n).keys()].map<PieceSpec>((i) => ({
      key: `${inner ? 'tube' : 'tyre'}${i}`,
      art: inner ? 'innerTube' : 'tyre',
      params: { outer, inner: inner ? g.rimBead - 8 : g.rimBead - 3, depth: c.spec.tyreMm, text: `${size}${folding ? ' · FOLDING' : ''}` },
      lie: { x: pitch / 2 + i * pitch, y: inner ? 60 : 85 },
      seat: pose([...(i === 0 ? g.front : g.rear), -c.spec.tyreMm / 2]),
      behavior: 'fly',
      explode: [0, 0, inner ? 100 : 180],
      layer: inner ? 3 : 6,
      extent: flat(-outer, -outer, outer, outer, 0, c.spec.tyreMm),
      order: i / n,
    })),
  }
}
