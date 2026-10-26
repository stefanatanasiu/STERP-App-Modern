import type { ExplosionLine } from '@/api/types'
import type { TreeNode } from '@/domain/tree'
import { aim, I3, pose, rotY, rotZ, type M3, type Pose, type V3 } from '../projection'
import type { Geometry, P2 } from '../geometry'
import type { BikeSpec } from '../spec'

// The catalogue turns one bill line into the pieces drawn for it: what each piece looks like (art), where it lies on the mat
// before the build (relative to the item's corner) and where it sits on the bike (bike coordinates, see geometry.ts). Counts
// come from the line's usage per parent (24 spokes per wheel, 6 inserts per frame); lengths of stock from its usage in metres.

export type ArtKind =
  | 'tube'
  | 'bead'
  | 'spool'
  | 'cylinder'
  | 'swarf'
  | 'tin'
  | 'decalSheet'
  | 'decal'
  | 'headsetBearing'
  | 'bolt'
  | 'bbShell'
  | 'bearing'
  | 'dropout'
  | 'bracket'
  | 'washer'
  | 'nut'
  | 'rim'
  | 'hub'
  | 'spoke'
  | 'nipple'
  | 'tapeRoll'
  | 'tapeRing'
  | 'rotor'
  | 'cassette'
  | 'tyre'
  | 'innerTube'
  | 'handlebar'
  | 'stem'
  | 'grip'
  | 'shifter'
  | 'bell'
  | 'crankset'
  | 'chain'
  | 'quickLink'
  | 'derailleur'
  | 'caliper'
  | 'coil'
  | 'route'
  | 'saddle'
  | 'seatpost'
  | 'pedal'
  | 'mudguard'
  | 'rack'
  | 'lamp'
  | 'kickstand'
  | 'reflector'
  | 'cage'
  | 'foam'
  | 'carton'
  | 'label'
  | 'manual'
  | 'cartridge'
  | 'dab'
  | 'ticket'
  | 'note'
  | 'part'

export type Behavior =
  /** moves from where it lies to its seat */
  | 'fly'
  /** shows up at its seat (welds, grease, rim tape and cables drawing on) */
  | 'appear'
  /** a supply or a process ticket: stays where it lies and pulses while it is used */
  | 'stay'
  /** used up: stays where it lies and fades while it is used (cable off its coil, an offcut) */
  | 'use'
  /** a by-product: shows up where it lies */
  | 'emit'
  /** an unrecognised part: flies into the middle of what it goes into and fades */
  | 'absorb'

export type Finish = 'alloy' | 'black' | 'silver' | 'rubber' | 'brass' | 'titanium' | 'gold' | 'steel'

export interface ArtParams {
  length?: number
  diameter?: number
  finish?: Finish
  r?: number
  inner?: number
  outer?: number
  teeth?: number[]
  zs?: number[]
  text?: string
  sub?: string
  color?: string
  points?: P2[]
  side?: 1 | -1
  which?: string
  holes?: number
  w?: number
  h?: number
  depth?: number
  icon?: string
  lines?: string[]
  head?: [number, number]
  shank?: [number, number]
  span?: [number, number]
}

export interface PieceSpec {
  key: string
  art: ArtKind
  params?: ArtParams
  /** where it lies on the mat: offset from the item's top-left corner (SVG directions), height and turn */
  lie: { x: number; y: number; z?: number; r?: M3 }
  seat?: Pose
  behavior: Behavior
  /** bike coordinates: it slides the last stretch in along this vector */
  approach?: V3
  /** bike coordinates: offset in the exploded view */
  explode?: V3
  layer: number
  /** painted with the frame or fork it is built into */
  coat?: boolean
  /** local bounding box of the art: min and max corners (y up) */
  extent: [V3, V3]
  /** 0..1: order within the item's action (staggered pieces) */
  order?: number
  /** turned in 3D between the mat and the bike: drawn from faces and bars updated every frame */
  turns?: boolean
  /** shown with the packaging only (assembled and exploded views) */
  packaging?: boolean
}

export interface ItemRecipe {
  pieces: PieceSpec[]
  /** footprint on the mat (mm) */
  size: [number, number]
}

/** What a made subassembly is, for where its parts go. */
export type AssemblyKind =
  | 'bike'
  | 'frame'
  | 'frontTriangle'
  | 'rearTriangle'
  | 'chainstays'
  | 'seatstays'
  | 'dropouts'
  | 'headset'
  | 'bb'
  | 'fork'
  | 'wheelset'
  | 'frontWheel'
  | 'rearWheel'
  | 'cockpit'
  | 'other'

export interface Ctx {
  g: Geometry
  spec: BikeSpec
  node: TreeNode
  line: ExplosionLine
  /** the made subassembly it is built into (phantoms skipped) */
  owner: AssemblyKind
  /** usage per parent rounded to a count of pieces (EA lines) */
  count: number
  /** this stock line among the stock lines of the same owner, thickest first (members are shared out between them) */
  stockIndex: number
  stockCount: number
}

export const text = (line: Pick<ExplosionLine, 'item' | 'description'>) => `${line.item} ${line.description}`.toUpperCase()

export function assemblyOf(line: Pick<ExplosionLine, 'item' | 'description' | 'level'>): AssemblyKind {
  if (line.level === 0) return 'bike'
  const t = text(line)
  if (/FRONT[- ]TRI/.test(t)) return 'frontTriangle'
  if (/REAR[- ]TRI/.test(t)) return 'rearTriangle'
  if (/CHAINSTAY/.test(t)) return 'chainstays'
  if (/SEATSTAY/.test(t)) return 'seatstays'
  if (/DROPOUT/.test(t)) return 'dropouts'
  if (/HEADSET/.test(t)) return 'headset'
  if (/\bBB\b|\bBB-|BOTTOM BRACKET/.test(t)) return 'bb'
  if (/FORK/.test(t)) return 'fork'
  if (/WHEELSET/.test(t)) return 'wheelset'
  if (/FRONT WHEEL|WHEEL-FRONT/.test(t)) return 'frontWheel'
  if (/REAR WHEEL|WHEEL-REAR/.test(t)) return 'rearWheel'
  if (/COCKPIT/.test(t)) return 'cockpit'
  if (/FRAME/.test(t)) return 'frame'
  return 'other'
}

// ---------------------------------------------------------------- helpers

/** Bounding box of flat artwork drawn in SVG coordinates (v down), as a local 3D box. */
export const flat = (u0: number, v0: number, u1: number, v1: number, z0 = 0, z1 = 0): [V3, V3] => [
  [u0, -v1, z0],
  [u1, -v0, z1],
]

export const seatAt = (p: P2, z = 0, r: M3 = I3): Pose => pose([p[0], p[1], z], r)
export const seatTurned = (p: P2, angle: number, z = 0): Pose => pose([p[0], p[1], z], rotZ(angle))
/** A seat whose local x axis runs from a to b (3D), facing the viewer. */
export const seatAlong = (a: V3, b: V3): Pose => pose(a, aim([b[0] - a[0], b[1] - a[1], b[2] - a[2]]))

/** Lying with the local z axis along the mat's x axis (an axle, a handlebar lying down). */
export const LIE_SIDEWAYS: M3 = rotY(Math.PI / 2)

/** A row of n pieces spaced dx apart. */
export const row = (n: number, dx: number, x0 = 0, y = 0) => [...Array(n).keys()].map((i) => ({ x: x0 + i * dx, y }))

/** A grid of n pieces, cols across. */
export const grid = (n: number, cols: number, dx: number, dy: number, x0 = 0, y0 = 0) =>
  [...Array(n).keys()].map((i) => ({ x: x0 + (i % cols) * dx, y: y0 + Math.floor(i / cols) * dy }))

/** "31.8 x 1.6 mm" -> 31.8; the first diameter in a description. */
export function diameterIn(description: string, fallback: number): number {
  const m = /(\d{2}(?:\.\d)?)\s*x\s*\d/i.exec(description)
  const v = m ? Number(m[1]) : NaN
  return Number.isFinite(v) ? v : fallback
}

/** "M5 x 16" -> [5, 16] */
export function threadIn(description: string, fallback: [number, number]): [number, number] {
  const m = /M(\d+)\s*x\s*(\d+)/i.exec(description)
  return m ? [Number(m[1]), Number(m[2])] : fallback
}

export const DEG = Math.PI / 180
