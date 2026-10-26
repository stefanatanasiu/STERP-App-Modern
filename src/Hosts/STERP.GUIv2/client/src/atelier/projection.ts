// The atelier draws in 2.5D: an oblique (cabinet-style) projection of a 3D scene onto the SVG plane. World axes: x to the right,
// y up, z toward the viewer, in millimetres. A plane parallel to the drawing (a wheel, a chainring, the frame seen from the side)
// keeps its true shape, and depth recedes up and to the right, so every flat part is an affine image of its 2D drawing: one SVG
// matrix per part, exact for any 3D pose. Only round bars seen along their length (a handlebar, an axle) need a computed outline.

export type V3 = readonly [number, number, number]
/** Row-major 3x3 rotation. */
export type M3 = readonly [number, number, number, number, number, number, number, number, number]
type Q = readonly [number, number, number, number]

export interface Pose {
  r: M3
  t: V3
}

/** SVG units added per millimetre of z (toward the viewer): nearer things sit lower and further left. */
export const DEPTH = { x: -0.3, y: 0.2 } as const

export const I3: M3 = [1, 0, 0, 0, 1, 0, 0, 0, 1]
export const ORIGIN: Pose = { r: I3, t: [0, 0, 0] }

export const add = (a: V3, b: V3, k = 1): V3 => [a[0] + b[0] * k, a[1] + b[1] * k, a[2] + b[2] * k]
export const sub = (a: V3, b: V3): V3 => [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
export const scale = (a: V3, k: number): V3 => [a[0] * k, a[1] * k, a[2] * k]
export const dot = (a: V3, b: V3) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
export const cross = (a: V3, b: V3): V3 => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
export const len = (a: V3) => Math.hypot(a[0], a[1], a[2])
export const norm = (a: V3): V3 => {
  const l = len(a) || 1
  return [a[0] / l, a[1] / l, a[2] / l]
}
export const lerp = (a: number, b: number, t: number) => a + (b - a) * t
export const lerp3 = (a: V3, b: V3, t: number): V3 => [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)]

export function mulM(a: M3, b: M3): M3 {
  return [
    a[0] * b[0] + a[1] * b[3] + a[2] * b[6],
    a[0] * b[1] + a[1] * b[4] + a[2] * b[7],
    a[0] * b[2] + a[1] * b[5] + a[2] * b[8],
    a[3] * b[0] + a[4] * b[3] + a[5] * b[6],
    a[3] * b[1] + a[4] * b[4] + a[5] * b[7],
    a[3] * b[2] + a[4] * b[5] + a[5] * b[8],
    a[6] * b[0] + a[7] * b[3] + a[8] * b[6],
    a[6] * b[1] + a[7] * b[4] + a[8] * b[7],
    a[6] * b[2] + a[7] * b[5] + a[8] * b[8],
  ]
}

export const mulV = (m: M3, v: V3): V3 => [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]]

export function rotX(a: number): M3 {
  const c = Math.cos(a)
  const s = Math.sin(a)
  return [1, 0, 0, 0, c, -s, 0, s, c]
}
export function rotY(a: number): M3 {
  const c = Math.cos(a)
  const s = Math.sin(a)
  return [c, 0, s, 0, 1, 0, -s, 0, c]
}
export function rotZ(a: number): M3 {
  const c = Math.cos(a)
  const s = Math.sin(a)
  return [c, -s, 0, s, c, 0, 0, 0, 1]
}

/** The rotation whose columns are the given axes (the images of x, y and z). */
export const basis = (x: V3, y: V3, z: V3): M3 => [x[0], y[0], z[0], x[1], y[1], z[1], x[2], y[2], z[2]]

export const pose = (t: V3, r: M3 = I3): Pose => ({ r, t })
export const compose = (a: Pose, b: Pose): Pose => ({ r: mulM(a.r, b.r), t: add(mulV(a.r, b.t), a.t) })
export const apply = (p: Pose, v: V3): V3 => add(mulV(p.r, v), p.t)
export const translate = (p: Pose, d: V3): Pose => ({ r: p.r, t: add(p.t, d) })

/** World point to SVG point. */
export const toSvg = (v: V3): [number, number] => [v[0] + v[2] * DEPTH.x, -v[1] + v[2] * DEPTH.y]
/** The world point at depth z that is drawn at the SVG point (x, y). */
export const fromSvg = (x: number, y: number, z = 0): V3 => [x - z * DEPTH.x, -(y - z * DEPTH.y), z]

/** The direction that projects to a point: planes containing it are seen edge-on. */
export const VIEW: V3 = norm([-DEPTH.x, DEPTH.y, 1])

/**
 * SVG matrix(a b c d e f) for 2D artwork drawn in a part's local x-y plane (SVG convention, v down: local (u, v) is (u, -v, 0)),
 * for the part at the given pose.
 */
export function svgMatrix(p: Pose): [number, number, number, number, number, number] {
  const r = p.r
  const a = r[0] + r[6] * DEPTH.x
  const b = -r[3] + r[6] * DEPTH.y
  const c = -r[1] - r[7] * DEPTH.x
  const d = r[4] - r[7] * DEPTH.y
  const [e, f] = toSvg(p.t)
  return [a, b, c, d, e, f]
}

export function matrixAttr(p: Pose): string {
  const m = svgMatrix(p)
  return `matrix(${m[0].toFixed(5)} ${m[1].toFixed(5)} ${m[2].toFixed(5)} ${m[3].toFixed(5)} ${m[4].toFixed(2)} ${m[5].toFixed(2)})`
}

export interface BarOutline {
  /** SVG start of the band and its direction (degrees) and length */
  x: number
  y: number
  angle: number
  length: number
  /** SVG transform of a unit circle onto the near end disc */
  cap: string
  /** how much the bar points at the viewer: 0 side-on, 1 end-on */
  endOn: number
}

/**
 * A round bar from a to b (in the pose's local frame) of radius r: the outline is a band along the projected axis, capped at
 * the near end by the projected end disc.
 */
export function barOutline(p: Pose, a: V3, b: V3, r: number): BarOutline {
  const wa = apply(p, a)
  const wb = apply(p, b)
  const [ax, ay] = toSvg(wa)
  const [bx, by] = toSvg(wb)
  const dx = bx - ax
  const dy = by - ay
  const axis = norm(sub(wb, wa))
  const helper: V3 = Math.abs(axis[1]) < 0.9 ? [0, 1, 0] : [1, 0, 0]
  const u = norm(cross(axis, helper))
  const v = cross(axis, u)
  const pu = [(u[0] + u[2] * DEPTH.x) * r, (-u[1] + u[2] * DEPTH.y) * r]
  const pv = [(v[0] + v[2] * DEPTH.x) * r, (-v[1] + v[2] * DEPTH.y) * r]
  const [cx, cy] = wb[2] >= wa[2] ? [bx, by] : [ax, ay]
  return {
    x: ax,
    y: ay,
    angle: (Math.atan2(dy, dx) * 180) / Math.PI,
    length: Math.hypot(dx, dy),
    cap: `matrix(${pu[0].toFixed(4)} ${pu[1].toFixed(4)} ${pv[0].toFixed(4)} ${pv[1].toFixed(4)} ${cx.toFixed(2)} ${cy.toFixed(2)})`,
    endOn: Math.abs(dot(axis, VIEW)),
  }
}

/** A rotation turning the x axis onto `dir`, with the local x-y plane facing the viewer as far as it can. */
export function aim(dir: V3): M3 {
  const x = norm(dir)
  let z = sub(VIEW, scale(x, dot(VIEW, x)))
  if (len(z) < 1e-6) z = [0, 0, 1]
  z = norm(z)
  const y = cross(z, x)
  return basis(x, y, z)
}

// ---------------------------------------------------------------- interpolation

function quatOf(m: M3): Q {
  const tr = m[0] + m[4] + m[8]
  if (tr > 0) {
    const s = Math.sqrt(tr + 1) * 2
    return [(m[7] - m[5]) / s, (m[2] - m[6]) / s, (m[3] - m[1]) / s, 0.25 * s]
  }
  if (m[0] > m[4] && m[0] > m[8]) {
    const s = Math.sqrt(1 + m[0] - m[4] - m[8]) * 2
    return [0.25 * s, (m[1] + m[3]) / s, (m[2] + m[6]) / s, (m[7] - m[5]) / s]
  }
  if (m[4] > m[8]) {
    const s = Math.sqrt(1 + m[4] - m[0] - m[8]) * 2
    return [(m[1] + m[3]) / s, 0.25 * s, (m[5] + m[7]) / s, (m[2] - m[6]) / s]
  }
  const s = Math.sqrt(1 + m[8] - m[0] - m[4]) * 2
  return [(m[2] + m[6]) / s, (m[5] + m[7]) / s, 0.25 * s, (m[3] - m[1]) / s]
}

function matOf(q: Q): M3 {
  const [x, y, z, w] = q
  return [
    1 - 2 * (y * y + z * z),
    2 * (x * y - z * w),
    2 * (x * z + y * w),
    2 * (x * y + z * w),
    1 - 2 * (x * x + z * z),
    2 * (y * z - x * w),
    2 * (x * z - y * w),
    2 * (y * z + x * w),
    1 - 2 * (x * x + y * y),
  ]
}

function slerp(a: Q, b: Q, t: number): Q {
  let cos = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
  let bb: Q = b
  if (cos < 0) {
    cos = -cos
    bb = [-b[0], -b[1], -b[2], -b[3]]
  }
  if (cos > 0.9995) {
    const q: Q = [lerp(a[0], bb[0], t), lerp(a[1], bb[1], t), lerp(a[2], bb[2], t), lerp(a[3], bb[3], t)]
    const l = Math.hypot(q[0], q[1], q[2], q[3]) || 1
    return [q[0] / l, q[1] / l, q[2] / l, q[3] / l]
  }
  const theta = Math.acos(cos)
  const s = Math.sin(theta)
  const ka = Math.sin((1 - t) * theta) / s
  const kb = Math.sin(t * theta) / s
  return [a[0] * ka + bb[0] * kb, a[1] * ka + bb[1] * kb, a[2] * ka + bb[2] * kb, a[3] * ka + bb[3] * kb]
}

const sameM = (a: M3, b: M3) => a === b || a.every((v, i) => Math.abs(v - b[i]) < 1e-9)

/** Rotation from a to b at t (shortest arc). */
export function slerpM(a: M3, b: M3, t: number): M3 {
  if (t <= 0) return a
  if (t >= 1) return b
  if (sameM(a, b)) return a
  return matOf(slerp(quatOf(a), quatOf(b), t))
}

export function lerpPose(a: Pose, b: Pose, t: number): Pose {
  return { r: slerpM(a.r, b.r, t), t: lerp3(a.t, b.t, t) }
}

// ---------------------------------------------------------------- easing

export const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v)
export const smooth = (t: number) => t * t * (3 - 2 * t)
export const easeInOut = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2)
export const easeOut = (t: number) => 1 - (1 - t) ** 3
/** Ease out with a slight overshoot: a part seats with a click. */
export function easeOutBack(t: number): number {
  const c1 = 1.2
  const c3 = c1 + 1
  return 1 + c3 * (t - 1) ** 3 + c1 * (t - 1) ** 2
}
