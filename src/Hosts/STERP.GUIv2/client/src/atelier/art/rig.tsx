import { createContext, useContext, useLayoutEffect, useMemo, useRef, type CSSProperties, type ReactNode } from 'react'
import { barOutline, compose, DEPTH, I3, matrixAttr, pose, type M3, type Pose, type V3 } from '../projection'

// Every piece is drawn from faces (flat artwork in a plane of the piece) and bars (round bars between two points of the piece).
// The scene moves a piece by handing its rig the piece's pose; the rig re-projects each face (one SVG matrix) and each bar (a
// band along the projected axis and the near end disc). Nothing re-renders while the build plays.

interface FaceReg {
  el: SVGGElement
  local: Pose | null
}

interface BarReg {
  band: SVGGElement
  body: SVGRectElement
  cap: SVGGElement | null
  a: V3
  b: V3
  r: number
}

export class Rig {
  faces = new Set<FaceReg>()
  bars = new Set<BarReg>()
  /** set when a face or bar registers: the scene re-applies the pose */
  dirty = true

  apply(p: Pose) {
    for (const f of this.faces) f.el.setAttribute('transform', matrixAttr(f.local ? compose(p, f.local) : p))
    for (const b of this.bars) {
      const o = barOutline(p, b.a, b.b, b.r)
      b.band.setAttribute('transform', `translate(${o.x.toFixed(2)} ${o.y.toFixed(2)}) rotate(${o.angle.toFixed(3)})`)
      b.body.setAttribute('width', Math.max(0.01, o.length).toFixed(2))
      if (b.cap) b.cap.setAttribute('transform', o.cap)
    }
    this.dirty = false
  }
}

export const RigContext = createContext<Rig | null>(null)

const key3 = (v: V3) => `${v[0]},${v[1]},${v[2]}`

/** Flat artwork in the piece's x-y plane, moved to depth z (or to `at`) and turned by `r`. */
export function Face({ z = 0, at, r, children, className, style }: { z?: number; at?: V3; r?: M3; children: ReactNode; className?: string; style?: CSSProperties }) {
  const rig = useContext(RigContext)!
  const ref = useRef<SVGGElement>(null)
  const atKey = at ? key3(at) : ''
  const rKey = r ? r.join(',') : ''
  const local = useMemo(() => (z || at || r ? pose(at ?? [0, 0, z], r ?? I3) : null), [z, atKey, rKey]) // eslint-disable-line react-hooks/exhaustive-deps
  useLayoutEffect(() => {
    const reg: FaceReg = { el: ref.current!, local }
    rig.faces.add(reg)
    rig.dirty = true
    return () => void rig.faces.delete(reg)
  }, [rig, local])
  return (
    <g ref={ref} className={className} style={style}>
      {children}
    </g>
  )
}

/**
 * A round bar from a to b (piece coordinates) of radius r. The band takes `fill` (a vertical gradient shades it as a
 * cylinder); the near end shows `cap` drawn on the unit circle (default: a disc in `capFill`).
 */
export function Bar({
  a,
  b,
  r,
  fill,
  cap,
  capFill,
  round = 0.3,
  className,
  style,
}: {
  a: V3
  b: V3
  r: number
  fill: string
  cap?: ReactNode | false
  capFill?: string
  round?: number
  className?: string
  style?: CSSProperties
}) {
  const rig = useContext(RigContext)!
  const band = useRef<SVGGElement>(null)
  const body = useRef<SVGRectElement>(null)
  const capRef = useRef<SVGGElement>(null)
  const ka = key3(a)
  const kb = key3(b)
  useLayoutEffect(() => {
    const reg: BarReg = { band: band.current!, body: body.current!, cap: capRef.current, a, b, r }
    rig.bars.add(reg)
    rig.dirty = true
    return () => void rig.bars.delete(reg)
  }, [rig, ka, kb, r]) // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <g className={className} style={style}>
      <g ref={band}>
        <rect ref={body} y={-r} height={r * 2} width={0} rx={r * round} fill={fill} />
      </g>
      {cap !== false && <g ref={capRef}>{cap ?? <circle r={1} fill={capFill ?? fill} />}</g>}
    </g>
  )
}

/**
 * A drum standing on the piece's x-y plane (axis toward the viewer): the side as seen past the top face, then the top face
 * (children) at height h. For pieces that do not turn.
 */
export function Drum({ r, h, side, edge, children }: { r: number; h: number; side: string; edge?: string; children: ReactNode }) {
  const ox = h * DEPTH.x
  const oy = h * DEPTH.y
  const l = Math.hypot(ox, oy) || 1
  const nx = (-oy / l) * r
  const ny = (ox / l) * r
  return (
    <>
      <Face>
        <circle r={r} fill={side} stroke={edge} strokeWidth={edge ? 1 : 0} />
        <polygon points={`${nx},${ny} ${nx + ox},${ny + oy} ${-nx + ox},${-ny + oy} ${-nx},${-ny}`} fill={side} />
      </Face>
      <Face z={h}>{children}</Face>
    </>
  )
}

/** A block standing on the piece's plane (w x h footprint, depth toward the viewer): side band, then the top face. */
export function Block({ w, h, depth, side, rx = 6, children }: { w: number; h: number; depth: number; side: string; rx?: number; children: ReactNode }) {
  const ox = depth * DEPTH.x
  const oy = depth * DEPTH.y
  return (
    <>
      <Face>
        <rect x={-w / 2} y={-h / 2} width={w} height={h} rx={rx} fill={side} />
        <polygon points={`${-w / 2},${-h / 2} ${-w / 2 + ox},${-h / 2 + oy} ${w / 2 + ox},${-h / 2 + oy} ${w / 2},${-h / 2}`} fill={side} />
        <polygon points={`${w / 2},${-h / 2} ${w / 2 + ox},${-h / 2 + oy} ${w / 2 + ox},${h / 2 + oy} ${w / 2},${h / 2}`} fill={side} />
      </Face>
      <Face z={depth}>{children}</Face>
    </>
  )
}
