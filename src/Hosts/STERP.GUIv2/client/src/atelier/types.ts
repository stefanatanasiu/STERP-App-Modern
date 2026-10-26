import type { ExplosionLine } from '@/api/types'
import type { TreeNode } from '@/domain/tree'
import type { AssemblyKind, PieceSpec, Role } from './catalog'
import type { Geometry } from './geometry'
import type { Pose, V3 } from './projection'
import type { BikeSpec } from './spec'

export interface Box {
  x: number
  y: number
  w: number
  h: number
}

export interface Piece extends PieceSpec {
  id: string
  node: ANode
  /** where it lies on the mat (world) */
  knolled: Pose
  /** global progress window in which it moves, shows up or is used */
  window: [number, number]
}

/** One occurrence of the explosion as the atelier stages it. */
export interface ANode {
  id: string
  tree: TreeNode
  line: ExplosionLine
  role: Role
  name: string
  color: string
  depth: number
  /** the find number of the occurrence in its parent bill ("10", "BP10") */
  find: string
  parent: ANode | null
  children: ANode[]
  /** the made subassembly (or the bike) it is built into: phantoms are transparent */
  owner: ANode | null
  assembly: AssemblyKind
  pieces: Piece[]
  /** space-separated ids of itself and every ancestor (highlighting a subassembly lights everything in it) */
  path: string
  /** leaf lines: the footprint on the mat (with the caption) */
  slot?: Box
  /** phantom kits: the tray their parts lie in */
  tray?: Box
  /** made subassemblies and the bike: their cell and the build area inside it */
  cell?: Box
  area?: Box
  /** made subassemblies: the pose of their bike frame on the mat while they are built */
  home?: Pose
  /** assembled bounds in bike coordinates (made nodes and the bike) */
  bounds?: { min: V3; max: V3 }
  /** when it is fitted, used or done (inside its owner's step); for a made node, when it is mounted into its owner */
  action?: [number, number]
  /** made nodes and the bike: the step in which their parts come together */
  build?: [number, number]
}

export interface Step {
  id: string
  /** the made node being built, or (final assembly) the line being fitted */
  node: ANode
  title: string
  start: number
  end: number
  color: string
  camera: Box
}

/** A process or coat applied to pieces over a window (paint, primer, heat treatment, anodising). */
export interface Effect {
  variable: '--primer' | '--paint' | '--heat' | '--anod'
  pieces: Piece[]
  window: [number, number]
  /** sweeps along x: each piece starts a little later than the one before it */
  sweep: boolean
}

export interface AtelierModel {
  spec: BikeSpec
  g: Geometry
  root: ANode
  nodes: ANode[]
  byId: Map<string, ANode>
  pieces: Piece[]
  steps: Step[]
  effects: Effect[]
  /** the whole mat and the bay where the bike is assembled (SVG coordinates) */
  mat: Box
  bay: Box
  bayPose: Pose
  /** beats of the whole timeline (playback speed) */
  beats: number
}
