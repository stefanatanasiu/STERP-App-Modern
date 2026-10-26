import { pose, rotY, rotZ, type V3 } from '../projection'
import { add2, at, type Geometry, type P2 } from '../geometry'
import { bosses } from './frame'
import { DEG, flat, grid, seatAt, text, type Ctx, type ItemRecipe, type PieceSpec } from './types'

// Accessories, grease, packaging, the services' job tickets, the work instruction and anything the catalogue does not know.

export function mudguards(c: Ctx): ItemRecipe {
  const g = c.g
  const r = g.wheelR + 18
  const sets: { key: string; axle: P2; span: [number, number] }[] = [
    { key: 'front', axle: g.front, span: [-8, 165] },
    { key: 'rear', axle: g.rear, span: [22, 205] },
  ]
  return {
    size: [r * 2 + 40, r + 120],
    pieces: sets.map<PieceSpec>((s, i) => ({
      key: s.key,
      art: 'mudguard',
      params: { r, span: s.span, depth: 45, which: s.key },
      lie: { x: r + 20 + i * 24, y: r + 40 + i * 50 },
      seat: seatAt(s.axle, -22),
      behavior: 'fly',
      approach: [0, 90, 0],
      explode: [i === 0 ? 90 : -90, 140, 0],
      layer: 8,
      extent: flat(-r - 8, -r - 8, r + 8, r * 0.3, 0, 45),
      order: i / 2,
    })),
  }
}

export function rack(c: Ctx): ItemRecipe {
  const g = c.g
  const h = g.wheelR + 46
  const mount = add2(g.seatCluster, [g.rear[0] - g.seatCluster[0], g.rear[1] - g.seatCluster[1]], 0.18)
  const eyelet: P2 = [-28, -24]
  const arm: P2 = [mount[0] - g.rear[0], mount[1] - g.rear[1]]
  return {
    size: [430, h + 60],
    pieces: [
      {
        key: 'rack',
        art: 'rack',
        params: { h, points: [eyelet, arm], w: 72 },
        lie: { x: 270, y: h + 34 },
        seat: seatAt(g.rear),
        behavior: 'fly',
        approach: [0, 120, 0],
        explode: [-110, 210, 0],
        layer: 7,
        extent: [
          [-262, -30, -80],
          [Math.max(140, arm[0] + 10), h + 12, 80],
        ],
      },
    ],
  }
}

export function lamps(c: Ctx): ItemRecipe {
  const g = c.g
  const rearAt = add2(g.postTop, g.seatDir, -150)
  return {
    size: [250, 80],
    pieces: [
      {
        key: 'front',
        art: 'lamp',
        params: { which: 'front', length: 82, r: 17 },
        lie: { x: 20, y: 40 },
        seat: pose([g.bar[0] - 26, g.bar[1] + 26, 62]),
        behavior: 'fly',
        approach: [0, 80, 0],
        explode: [140, 60, 0],
        layer: 66,
        turns: true,
        extent: [
          [0, -18, -18],
          [82, 18, 18],
        ],
      },
      {
        key: 'rear',
        art: 'lamp',
        params: { which: 'rear', length: 58, r: 15 },
        lie: { x: 150, y: 40 },
        seat: pose([rearAt[0] - 16, rearAt[1], 0], rotY(Math.PI)),
        behavior: 'fly',
        approach: [-80, 0, 0],
        explode: [-140, 0, 0],
        layer: 20,
        turns: true,
        extent: [
          [0, -16, -16],
          [58, 16, 16],
        ],
        order: 0.5,
      },
    ],
  }
}

export function kickstand(c: Ctx): ItemRecipe {
  const g = c.g
  const mount = add2(g.rear, [62, -18])
  const foot: P2 = [mount[0] - 95, 6]
  const length = Math.hypot(foot[0] - mount[0], foot[1] - mount[1])
  return {
    size: [length + 40, 70],
    pieces: [
      {
        key: 'kickstand',
        art: 'kickstand',
        params: { length },
        lie: { x: 24, y: 34 },
        seat: pose(at(mount, -66), rotZ(Math.atan2(foot[1] - mount[1], foot[0] - mount[0]))),
        behavior: 'fly',
        approach: [0, 0, -100],
        explode: [0, -120, -60],
        layer: -66,
        extent: flat(-22, -22, length + 16, 22),
      },
    ],
  }
}

/** The reflector set: front, rear, one on each face of both pedals and one on each wheel's spokes. */
export function reflectors(c: Ctx): ItemRecipe {
  const g = c.g
  const pedalFaces: { at: P2; z: number }[] = [
    { at: add2(g.pedalRight, [50, 0]), z: 132 },
    { at: add2(g.pedalRight, [-50, 0]), z: 132 },
    { at: add2(g.pedalLeft, [50, 0]), z: -132 },
    { at: add2(g.pedalLeft, [-50, 0]), z: -132 },
  ]
  const spot = (axle: P2, a: number): P2 => [axle[0] + Math.cos(a * DEG) * 205, axle[1] + Math.sin(a * DEG) * 205]
  const seats: { key: string; which: string; seat: ReturnType<typeof pose>; layer: number }[] = [
    { key: 'front', which: 'white', seat: pose([g.bar[0] + 30, g.bar[1] - 30, 0], rotY(70 * DEG)), layer: 47 },
    { key: 'rear', which: 'red', seat: pose(at(add2(g.postTop, g.seatDir, -205)), rotY(-70 * DEG)), layer: 21 },
    ...pedalFaces.map((p, i) => ({ key: `pedal${i}`, which: 'amber', seat: pose(at(p.at, p.z), rotY((i % 2 === 0 ? 80 : -80) * DEG)), layer: p.z })),
    { key: 'wheelFront', which: 'amber', seat: pose(at(spot(g.front, 30), 8), rotZ(30 * DEG)), layer: 24 },
    { key: 'wheelRear', which: 'amber', seat: pose(at(spot(g.rear, 210), 8), rotZ(210 * DEG)), layer: 24 },
  ]
  const spots = grid(seats.length, 4, 56, 56, 28, 28)
  return {
    size: [4 * 56, 2 * 56],
    pieces: seats.map<PieceSpec>((s, i) => ({
      key: s.key,
      art: 'reflector',
      params: { which: s.which },
      lie: spots[i],
      seat: s.seat,
      behavior: 'fly',
      layer: s.layer,
      turns: true,
      extent: flat(-20, -12, 20, 12),
      order: i / seats.length,
    })),
  }
}

export function bottleCage(c: Ctx): ItemRecipe {
  const g = c.g
  const [lower, upper] = bosses(g)
  const angle = Math.atan2(upper.p[1] - lower.p[1], upper.p[0] - lower.p[0])
  return {
    size: [200, 100],
    pieces: [
      {
        key: 'cage',
        art: 'cage',
        lie: { x: 24, y: 80 },
        seat: pose([lower.p[0], lower.p[1], 0], rotZ(angle)),
        behavior: 'fly',
        approach: [-lower.axis[0] * 90, -lower.axis[1] * 90, 0],
        explode: [-lower.axis[0] * 110, -lower.axis[1] * 110, 0],
        layer: 25,
        extent: flat(-24, -78, 160, 4, -38, 38),
      },
    ],
  }
}

// ---------------------------------------------------------------- consumables

/** Grease: the cartridge serves the whole batch; dabs appear where the bike is greased as it is assembled. */
export function grease(c: Ctx): ItemRecipe {
  const g = c.g
  const spots: V3[] = [at(g.bb, 42), at(add2(g.headTop, g.headDir, -10), 20), at(add2(g.seatTop, g.seatDir, -8), 16), at(g.pedalRight, 84), at(g.pedalLeft, -84)]
  return {
    size: [250, 70],
    pieces: [
      { key: 'cartridge', art: 'cartridge', params: { length: 230, r: 27, text: 'LITHIUM EP2' }, lie: { x: 10, y: 34 }, behavior: 'stay', layer: 0, extent: flat(0, -27, 230, 27) },
      ...spots.map<PieceSpec>((p, i) => ({
        key: `dab${i}`,
        art: 'dab',
        lie: { x: 120, y: 34 },
        seat: pose(p),
        behavior: 'appear',
        layer: p[2] + 6,
        extent: flat(-7, -5, 7, 5),
        order: i / spots.length,
      })),
    ],
  }
}

// ---------------------------------------------------------------- packaging

export function cartonBox(g: Geometry) {
  const x0 = g.rear[0] - g.wheelR - 70
  const x1 = g.front[0] + g.wheelR + 70
  const y0 = -40
  const y1 = Math.max(g.saddle[1] + 70, g.bar[1] + 60) + 50
  return { x0, x1, y0, y1, w: x1 - x0, h: y1 - y0, depth: 280, cx: (x0 + x1) / 2, cy: (y0 + y1) / 2 }
}

export function carton(c: Ctx): ItemRecipe {
  const box = cartonBox(c.g)
  const w = 1480
  const h = 800
  return {
    size: [w, h],
    pieces: [
      {
        key: 'carton',
        art: 'carton',
        params: { w, h, depth: box.depth, text: c.spec.brand.toUpperCase(), points: [[box.w, box.h]] },
        lie: { x: w / 2, y: h / 2 },
        seat: pose([box.cx, box.cy, -box.depth / 2]),
        behavior: 'fly',
        layer: -200,
        packaging: true,
        extent: flat(-box.w / 2, -box.h / 2, box.w / 2, box.h / 2, 0, box.depth),
      },
    ],
  }
}

export function foam(c: Ctx): ItemRecipe {
  const g = c.g
  const n = Math.max(1, c.count)
  const seats: V3[] = [at(g.front, 30), at(g.rear, 74), at(g.bb, 104), at(add2(g.topStart, [g.topEnd[0] - g.topStart[0], g.topEnd[1] - g.topStart[1]], 0.5), 24)]
  return {
    size: [Math.min(n, 2) * 130, Math.ceil(n / 2) * 80],
    pieces: grid(n, 2, 130, 80, 65, 40).map<PieceSpec>((spot, i) => ({
      key: `foam${i}`,
      art: 'foam',
      params: { w: 110, h: 60, depth: 40 },
      lie: spot,
      seat: pose(seats[i % seats.length]),
      behavior: 'fly',
      layer: 140,
      packaging: true,
      extent: flat(-55, -30, 55, 30, 0, 40),
      order: i / n,
    })),
  }
}

export function shippingLabel(c: Ctx): ItemRecipe {
  const box = cartonBox(c.g)
  return {
    size: [110, 150],
    pieces: [
      {
        key: 'label',
        art: 'label',
        params: { w: 105, h: 148, text: c.spec.brand.toUpperCase() },
        lie: { x: 2, y: 1 },
        seat: pose([box.x1 - 190, box.y1 - 40, box.depth / 2 + 1], rotZ(-3 * DEG)),
        behavior: 'fly',
        layer: 200,
        packaging: true,
        extent: flat(0, 0, 105, 148),
      },
    ],
  }
}

export function manual(c: Ctx): ItemRecipe {
  const box = cartonBox(c.g)
  return {
    size: [160, 220],
    pieces: [
      {
        key: 'manual',
        art: 'manual',
        params: { w: 148, h: 210, text: c.spec.brand },
        lie: { x: 6, y: 5 },
        seat: pose([box.x0 + 90, box.y0 + 330, -90], rotZ(4 * DEG)),
        behavior: 'fly',
        layer: -150,
        packaging: true,
        extent: flat(0, 0, 148, 210),
      },
    ],
  }
}

// ---------------------------------------------------------------- services, notes and the unknown

export function serviceIcon(t: string): string {
  if (/HEAT|T6/.test(t)) return 'heat'
  if (/ANODIS|ANODIZ/.test(t)) return 'anodise'
  if (/INSPECT|CERTIF|QC/.test(t)) return 'inspect'
  if (/PAINT|POWDER/.test(t)) return 'paint'
  return 'service'
}

/** A job ticket for a service. Inspection hangs its ticket on the handlebar as the QC tag. */
export function ticket(c: Ctx, name: string): ItemRecipe {
  const t = text(c.line)
  const icon = serviceIcon(t)
  const tag = icon === 'inspect' && c.owner === 'bike'
  return {
    size: [170, tag ? 150 : 110],
    pieces: [
      {
        key: 'ticket',
        art: 'ticket',
        params: { icon, text: name, sub: c.line.item, which: tag ? 'tag' : 'ticket' },
        lie: { x: 18, y: tag ? 26 : 10 },
        seat: tag ? pose([c.g.bar[0] - 6, c.g.bar[1] - 10, -(c.g.halfBar - 110)], rotZ(-4 * DEG)) : undefined,
        behavior: tag ? 'fly' : 'stay',
        layer: tag ? 70 : 0,
        extent: tag ? flat(-10, -4, 140, 124) : flat(0, 0, 140, 90),
      },
    ],
  }
}

export function note(c: Ctx): ItemRecipe {
  const words = c.line.description.split(/\s+/)
  const lines: string[] = []
  for (const w of words) {
    const last = lines[lines.length - 1]
    if (last && (last + ' ' + w).length <= 26) lines[lines.length - 1] = `${last} ${w}`
    else lines.push(w)
  }
  return {
    size: [230, 160],
    pieces: [{ key: 'note', art: 'note', params: { lines: lines.slice(0, 6), text: c.line.item }, lie: { x: 4, y: 6 }, behavior: 'stay', layer: 0, extent: flat(0, 0, 220, 150) }],
  }
}

export function part(c: Ctx, name: string, behavior: 'absorb' | 'stay' | 'emit' = 'absorb'): ItemRecipe {
  return {
    size: [190, 100],
    pieces: [{ key: 'part', art: 'part', params: { text: c.line.item, sub: name }, lie: { x: 4, y: 4 }, behavior, layer: 0, extent: flat(0, 0, 180, 90) }],
  }
}
