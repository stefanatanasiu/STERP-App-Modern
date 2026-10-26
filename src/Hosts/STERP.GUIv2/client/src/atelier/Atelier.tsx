import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { cn } from '@/components/ui/primitives'
import { Art } from './art'
import { AtelierDefs } from './art/defs'
import { Rig, RigContext } from './art/rig'
import { effectsAt, pieceAt, stepIndexAt, viewBox, type CoatVar, type PieceState, type View } from './evaluate'
import { easeInOut, lerp, lerpPose, toSvg, type M3, type V3 } from './projection'
import { Guides } from './scene/Guides'
import { Mat, type CaptionText } from './scene/Mat'
import { SECONDS_PER_BEAT, useAtelierTimeline } from './store'
import type { ANode, AtelierModel, Box, Piece } from './types'

// The atelier stage: one SVG, a camera (pan and zoom, or following the build), the mat, every piece of the explosion and the
// effects of the build. React renders the pieces once; a frame loop poses them from the timeline (evaluate.ts) without
// re-rendering, so the build plays smoothly with hundreds of parts.

export interface StageInsets {
  left: number
  right: number
  top: number
  bottom: number
}

export interface AtelierProps {
  model: AtelierModel
  view: View
  blueprint: boolean
  packaging: boolean
  lit: boolean
  follow: boolean
  /** the node in focus (selected or hovered): it and everything in it stay lit, the rest dims */
  focus: string | null
  onHover: (id: string | null) => void
  onSelect: (id: string | null) => void
  caption: (n: ANode) => CaptionText
  tooltip: (n: ANode) => ReactNode
  fitToken: number
  insets: StageInsets
}

interface Reg {
  rig: Rig
  el: SVGGElement
  t: V3 | null
  r: M3 | null
  opacity: number
  m: number
  vars: Partial<Record<CoatVar, number>>
}

const REDUCED = typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

const PieceView = memo(function PieceView({ piece, register }: { piece: Piece; register: (id: string, rig: Rig | null, el: SVGGElement | null) => void }) {
  const rig = useMemo(() => new Rig(), [])
  const ref = useRef<SVGGElement>(null)
  useLayoutEffect(() => {
    register(piece.id, rig, ref.current)
    return () => register(piece.id, null, null)
  }, [piece.id, rig, register])
  return (
    <g ref={ref} className="pc" data-node={piece.node.id} data-path={piece.node.path}>
      <g className="pcd">
        <RigContext.Provider value={rig}>
          <Art piece={piece} />
        </RigContext.Provider>
      </g>
    </g>
  )
})

function sameV(a: readonly number[] | null, b: readonly number[]) {
  if (!a) return false
  for (let i = 0; i < b.length; i++) if (Math.abs(a[i] - b[i]) > 1e-4) return false
  return true
}

export function Atelier(props: AtelierProps) {
  const { model, view, blueprint, packaging, lit, focus, caption, tooltip, fitToken } = props
  const host = useRef<HTMLDivElement>(null)
  const world = useRef<SVGGElement>(null)
  const worldMat = useRef<SVGGElement>(null)
  const guidesWrap = useRef<SVGGElement>(null)
  const fx = useRef<SVGGElement>(null)
  const tip = useRef<HTMLDivElement>(null)
  const regs = useRef(new Map<string, Reg>()).current
  const propsRef = useRef(props)
  propsRef.current = props
  const [active, setActive] = useState('')
  const [hover, setHover] = useState<string | null>(null)

  const register = useCallback(
    (id: string, rig: Rig | null, el: SVGGElement | null) => {
      if (rig && el) regs.set(id, { rig, el, t: null, r: null, opacity: -1, m: -1, vars: {} })
      else regs.delete(id)
    },
    [regs],
  )

  // pieces in the bike drawn by depth; the ones moving in the current steps drawn last, above the mat
  const ordered = useMemo(() => {
    const ids = active ? active.split(' ') : []
    const moving = (pc: Piece) => ids.some((id) => pc.node.path.split(' ').includes(id))
    return [...model.pieces].sort((a, b) => Number(moving(a)) - Number(moving(b)) || a.layer - b.layer)
  }, [model, active])

  const boxes = useMemo(
    () => ({ assembled: viewBox(model, 'assembled', false), packed: viewBox(model, 'assembled', true), exploded: viewBox(model, 'exploded', false), explodedPacked: viewBox(model, 'exploded', true) }),
    [model],
  )

  // camera: the world point at the stage centre and the zoom (px per mm)
  const cam = useRef({ cx: 0, cy: 0, k: 0, manual: false, w: 1, h: 1, hs: 0, cs: 0, lod: '', tx: NaN, ty: NaN, kApplied: 0, frameMs: 16, busyUntil: 0 })
  useEffect(() => {
    cam.current.manual = false
  }, [fitToken, view])

  // a view change blends every piece from where it was to where the new view puts it
  const blend = useRef<{ t: number; from: Map<string, PieceState> | null }>({ t: 1, from: null })
  const lastView = useRef({ view, packaging })
  const states = useRef(new Map<string, PieceState>())
  useEffect(() => {
    if (lastView.current.view === view && lastView.current.packaging === packaging) return
    lastView.current = { view, packaging }
    blend.current = { t: 0, from: new Map(states.current) }
  }, [view, packaging])

  // the frame loop
  useEffect(() => {
    let raf = 0
    let last = performance.now()
    let lastP = -1
    let lastKey = ''
    let frame = 0
    const pulses: { x: number; y: number; t0: number; color: string; big: boolean }[] = []
    const sparkEls = () => Array.from(fx.current?.querySelectorAll<SVGLineElement>('.spark') ?? [])
    const pulseEls = () => Array.from(fx.current?.querySelectorAll<SVGCircleElement>('.pulse') ?? [])

    const loop = (now: number) => {
      raf = requestAnimationFrame(loop)
      const dt = Math.min(0.1, (now - last) / 1000)
      last = now
      frame++
      const P = propsRef.current
      const tl = useAtelierTimeline.getState()
      if (tl.playing && P.view === 'build') tl.tick(dt, model.beats * SECONDS_PER_BEAT)
      const p = useAtelierTimeline.getState().progress
      const b = blend.current
      if (b.t < 1) b.t = Math.min(1, b.t + dt / 0.9)
      const blending = b.t < 1
      let dirty = false
      for (const r of regs.values()) if (r.rig.dirty) dirty = true
      let piecesMoved = false

      // pieces
      if (p !== lastP || blending || dirty || b.from) {
        const fxVars = effectsAt(model, p, P.view)
        const e = easeInOut(b.t)
        for (const pc of model.pieces) {
          const reg = regs.get(pc.id)
          if (!reg) continue
          let s = pieceAt(model, pc, p, P.view, P.packaging)
          states.current.set(pc.id, s)
          const from = b.from?.get(pc.id)
          if (blending && from) s = { pose: lerpPose(from.pose, s.pose, e), opacity: lerp(from.opacity, s.opacity, e), m: lerp(from.m, s.m, e) }
          if (reg.rig.dirty || !sameV(reg.t, s.pose.t) || !sameV(reg.r, s.pose.r)) {
            reg.rig.apply(s.pose)
            reg.t = s.pose.t
            reg.r = s.pose.r
            if (s.opacity > 0) piecesMoved = true
          }
          const op = Math.round(s.opacity * 100) / 100
          if (op !== reg.opacity) {
            reg.el.style.opacity = String(op)
            reg.el.style.display = op <= 0 ? 'none' : ''
            reg.opacity = op
          }
          const m = Math.round(s.m * 200) / 200
          if (m !== reg.m) {
            reg.el.style.setProperty('--m', String(m))
            reg.m = m
          }
          const vars = fxVars.get(pc)
          if (vars)
            for (const k of Object.keys(vars) as CoatVar[]) {
              const v = Math.round((vars[k] ?? 0) * 100) / 100
              if (reg.vars[k] !== v) {
                reg.el.style.setProperty(k, String(v))
                reg.vars[k] = v
              }
            }
        }
        if (!blending) b.from = null

        // landing pulses for parts that just seated while playing forward
        if (!REDUCED && P.view === 'build' && lastP >= 0 && p > lastP && p - lastP < 0.02) {
          for (const n of model.nodes) {
            const w = n.action
            if (!w || !(lastP < w[1] && p >= w[1])) continue
            const seated = n.role === 'make' ? n.pieces.length === 0 : n.pieces.some((pc) => pc.seat && pc.behavior === 'fly')
            if (!seated) continue
            const pcs = n.role === 'make' ? model.pieces.filter((pc) => pc.node.path.split(' ').includes(n.id) && pc.seat) : n.pieces.filter((pc) => pc.seat)
            if (!pcs.length) continue
            let x = 0
            let y = 0
            for (const pc of pcs) {
              const [sx, sy] = toSvg(states.current.get(pc.id)?.pose.t ?? pc.knolled.t)
              x += sx
              y += sy
            }
            pulses.push({ x: x / pcs.length, y: y / pcs.length, t0: now, color: n.color, big: n.role === 'make' })
            if (pulses.length > 10) pulses.shift()
          }
        }

        // what is moving now: drawn above everything else
        if (P.view === 'build') {
          const ids = model.nodes.filter((n) => n.action && p > n.action[0] && p < n.action[1] + 0.004).map((n) => n.id)
          const key = ids.join(' ')
          if (key !== lastKey) {
            lastKey = key
            setActive(key)
          }
        }
        lastP = p
      }

      // effects: pulses and weld sparks
      const pe = pulseEls()
      pe.forEach((el, i) => {
        const q = pulses[i]
        const age = q ? (now - q.t0) / 1000 : 9
        if (!q || age > 0.8) {
          el.setAttribute('opacity', '0')
          return
        }
        el.setAttribute('cx', q.x.toFixed(1))
        el.setAttribute('cy', q.y.toFixed(1))
        el.setAttribute('r', ((q.big ? 120 : 30) + age * (q.big ? 520 : 160)).toFixed(1))
        el.setAttribute('stroke', q.color)
        el.setAttribute('opacity', (1 - age / 0.8).toFixed(3))
      })
      const se = sparkEls()
      let si = 0
      if (!REDUCED && P.view === 'build') {
        for (const pc of model.pieces) {
          if (pc.art !== 'bead') continue
          const [w0, w1] = pc.window
          if (!(p > w0 && p < w1)) continue
          const [x, y] = toSvg(states.current.get(pc.id)?.pose.t ?? pc.knolled.t)
          for (let k = 0; k < 7 && si < se.length; k++, si++) {
            const a = ((frame * 37 + k * 53) % 360) * (Math.PI / 180)
            const l = 10 + ((frame * 13 + k * 29) % 30)
            const el = se[si]
            el.setAttribute('x1', (x + Math.cos(a) * 6).toFixed(1))
            el.setAttribute('y1', (y + Math.sin(a) * 6).toFixed(1))
            el.setAttribute('x2', (x + Math.cos(a) * l).toFixed(1))
            el.setAttribute('y2', (y + Math.sin(a) * l).toFixed(1))
            el.setAttribute('opacity', '1')
          }
        }
      }
      for (; si < se.length; si++) se[si].setAttribute('opacity', '0')

      // camera
      const el = host.current
      const g = world.current
      const gm = worldMat.current
      if (!el || !g || !gm) return
      const W = el.clientWidth
      const H = el.clientHeight
      const c = cam.current
      const ins = P.insets
      const sw = Math.max(80, W - ins.left - ins.right)
      const sh = Math.max(80, H - ins.top - ins.bottom)
      const target: Box =
        P.view === 'assembled'
          ? P.packaging
            ? boxes.packed
            : boxes.assembled
          : P.view === 'exploded'
            ? P.packaging
              ? boxes.explodedPacked
              : boxes.exploded
            : !P.follow || p <= 0
              ? model.mat
              : p >= 1
                ? P.packaging
                  ? boxes.packed
                  : boxes.assembled
                : model.steps[Math.min(model.steps.length - 1, stepIndexAt(model, p))].camera
      const tk = Math.min(sw / target.w, sh / target.h)
      const tcx = target.x + target.w / 2
      const tcy = target.y + target.h / 2
      if (c.k === 0 || W !== c.w || H !== c.h) {
        if (c.k === 0) Object.assign(c, { cx: tcx, cy: tcy, k: tk })
        c.w = W
        c.h = H
      }
      if (!c.manual) {
        const a = 1 - Math.exp(-dt * (REDUCED ? 30 : 2.6))
        c.cx += (tcx - c.cx) * a
        c.cy += (tcy - c.cy) * a
        c.k = Math.exp(lerp(Math.log(c.k), Math.log(tk), a))
      }
      const tx = ins.left + sw / 2 - c.cx * c.k
      const ty = ins.top + sh / 2 - c.cy * c.k
      // only touch the DOM when the view moved by more than a fraction of a pixel: a still camera repaints nothing
      const moved = Math.abs(tx - c.tx) > 0.05 || Math.abs(ty - c.ty) > 0.05 || Math.abs(c.k / (c.kApplied || 1) - 1) > 1e-4
      if (moved) {
        const m = `matrix(${c.k.toFixed(5)} 0 0 ${c.k.toFixed(5)} ${tx.toFixed(2)} ${ty.toFixed(2)})`
        g.setAttribute('transform', m)
        gm.setAttribute('transform', m)
        c.tx = tx
        c.ty = ty
        c.kApplied = c.k
      }
      // header and caption sizes follow the zoom in steps (a change restyles every header and caption)
      const hs = Math.min(3.2, Math.max(0.5, 15 / (30 * c.k)))
      const cs = Math.min(1.25, Math.max(0.55, 12 / (19 * c.k)))
      if (Math.abs(hs / (c.hs || 1) - 1) > 0.04) {
        gm.style.setProperty('--hs', hs.toFixed(3))
        guidesWrap.current?.style.setProperty('--hs', hs.toFixed(3))
        c.hs = hs
      }
      if (Math.abs(cs / (c.cs || 1) - 1) > 0.04) {
        gm.style.setProperty('--cs', cs.toFixed(3))
        c.cs = cs
      }
      const lod = c.k < 0.2 ? 'far' : c.k < 0.45 ? 'mid' : 'near'
      if (lod !== c.lod) {
        el.dataset.lod = lod
        c.lod = lod
      }
      // a device that cannot keep up drops the soft shadows while things move, and gets them back when they settle
      c.frameMs = c.frameMs * 0.92 + dt * 1000 * 0.08
      const busy = (piecesMoved || moved) && c.frameMs > 26
      if (busy) c.busyUntil = now + 300
      const flag = now < c.busyUntil ? '1' : ''
      if ((el.dataset.busy ?? '') !== flag) el.dataset.busy = flag
    }
    raf = requestAnimationFrame(loop)
    return () => cancelAnimationFrame(raf)
  }, [model, regs, boxes])

  // wheel zoom about the pointer, drag to pan
  useEffect(() => {
    const el = host.current
    if (!el) return
    const onWheel = (e: WheelEvent) => {
      e.preventDefault()
      const c = cam.current
      const ins = propsRef.current.insets
      const r = el.getBoundingClientRect()
      const sw = Math.max(80, r.width - ins.left - ins.right)
      const sh = Math.max(80, r.height - ins.top - ins.bottom)
      const sx = e.clientX - r.left
      const sy = e.clientY - r.top
      const wx = c.cx + (sx - (ins.left + sw / 2)) / c.k
      const wy = c.cy + (sy - (ins.top + sh / 2)) / c.k
      const k = Math.min(4, Math.max(0.04, c.k * Math.exp(-e.deltaY * 0.0016)))
      c.cx = wx - (sx - (ins.left + sw / 2)) / k
      c.cy = wy - (sy - (ins.top + sh / 2)) / k
      c.k = k
      c.manual = true
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [])

  const drag = useRef<{ x: number; y: number; moved: boolean; id: number } | null>(null)
  const idAt = (t: EventTarget | null): string | null => {
    const e = (t as Element | null)?.closest?.('[data-node],[data-cell]') as HTMLElement | SVGElement | null
    return e ? (e.getAttribute('data-node') ?? e.getAttribute('data-cell')) : null
  }

  const focusCss = useMemo(() => {
    if (!focus) return ''
    const leaf = model.byId.get(focus)
    const glow = leaf && leaf.pieces.length > 0 && leaf.pieces.length < 30 ? `.atelier .pc[data-node="${focus}"] > .pcd { filter: url(#glow); }` : ''
    return `.atelier.has-focus .pcd { opacity: 0.2; } .atelier.has-focus .cap { opacity: 0.25; }
      .atelier .pc[data-path~="${focus}"] > .pcd { opacity: 1; } .atelier .cap[data-node="${focus}"] { opacity: 1; }
      .atelier .cell[data-cell="${focus}"] > rect:first-child { stroke-opacity: 0.75; fill-opacity: 0.05; } ${glow}`
  }, [focus, model])

  const hovered = hover ? model.byId.get(hover) : null

  return (
    <div
      ref={host}
      className={cn('atelier relative size-full touch-none overflow-hidden select-none', `view-${view}`, blueprint && 'blueprint', lit && 'lit', focus && 'has-focus', hover ? 'cursor-pointer' : 'cursor-grab')}
      onPointerDown={(e) => {
        drag.current = { x: e.clientX, y: e.clientY, moved: false, id: e.pointerId }
      }}
      onPointerMove={(e) => {
        const d = drag.current
        if (d && e.buttons === 1) {
          const dx = e.clientX - d.x
          const dy = e.clientY - d.y
          if (d.moved || Math.hypot(dx, dy) > 4) {
            if (!d.moved) host.current?.setPointerCapture(d.id)
            d.moved = true
            const c = cam.current
            c.cx -= dx / c.k
            c.cy -= dy / c.k
            c.manual = true
            d.x = e.clientX
            d.y = e.clientY
          }
          return
        }
        const id = idAt(e.target)
        if (id !== hover) {
          setHover(id)
          props.onHover(id)
        }
        const t = tip.current
        const r = host.current?.getBoundingClientRect()
        if (t && r) t.style.transform = `translate(${e.clientX - r.left + 16}px, ${e.clientY - r.top + 18}px)`
      }}
      onPointerUp={(e) => {
        const d = drag.current
        drag.current = null
        if (d?.moved) {
          host.current?.releasePointerCapture(d.id)
          return
        }
        props.onSelect(idAt(e.target))
      }}
      onPointerLeave={() => {
        if (hover) {
          setHover(null)
          props.onHover(null)
        }
      }}
    >
      <svg className="absolute inset-0 size-full" style={{ fontFamily: FONT }}>
        <AtelierDefs spec={model.spec} />
        <style>{CSS + focusCss}</style>
        <g ref={worldMat} style={{ ['--lit' as string]: lit ? 1 : 0 }}>
          <Mat model={model} caption={caption} />
          <rect className="night" x={model.mat.x - 400} y={model.mat.y - 400} width={model.mat.w + 800} height={model.mat.h + 800} fill="#000308" pointerEvents="none" />
        </g>
      </svg>
      {/* the parts in a layer of their own: a part moving repaints this layer only, not the mat under it */}
      <svg className="pointer-events-none absolute inset-0 size-full" style={{ fontFamily: FONT, willChange: 'transform' }}>
        <g ref={world} style={{ ['--lit' as string]: lit ? 1 : 0 }}>
          <g className="pieces">
            {ordered.map((pc) => (
              <PieceView key={pc.id} piece={pc} register={register} />
            ))}
          </g>
          <g ref={fx} className="fx">
            {[...Array(10).keys()].map((k) => (
              <circle key={`p${k}`} className="pulse" r={0} fill="none" strokeWidth={5} opacity={0} />
            ))}
            {[...Array(24).keys()].map((k) => (
              <line key={`s${k}`} className="spark" stroke="#ffd27a" strokeWidth={2.4} strokeLinecap="round" opacity={0} />
            ))}
          </g>
          <g ref={guidesWrap} style={{ ['--hs' as string]: cam.current.hs || 1 }}>
            {view === 'exploded' && <Guides model={model} packaging={packaging} />}
          </g>
        </g>
      </svg>
      <div ref={tip} className="pointer-events-none absolute top-0 left-0 z-30" style={{ opacity: hovered && !drag.current?.moved ? 1 : 0, transition: 'opacity 120ms' }}>
        {hovered && tooltip(hovered)}
      </div>
    </div>
  )
}

const FONT = '"Inter Variable", "Segoe UI", system-ui, sans-serif'

const CSS = `
.atelier .pieces, .atelier .guides { pointer-events: visiblePainted; }
.atelier .pcd { transition: opacity 0.25s ease; }
.atelier .cap { transition: opacity 0.25s ease; }
.atelier[data-lod='far'] .captions, .atelier[data-lod='far'] .ticks text { display: none; }
.atelier .cell, .atelier .tray, .atelier .captions, .atelier .ticks, .atelier .bay-frame { transition: opacity 0.6s ease; }
.atelier.view-assembled .cell, .atelier.view-assembled .tray, .atelier.view-assembled .captions, .atelier.view-assembled .bay-frame,
.atelier.view-exploded .cell, .atelier.view-exploded .tray, .atelier.view-exploded .captions, .atelier.view-exploded .bay-frame { opacity: 0; pointer-events: none; }
.atelier .night { opacity: 0; transition: opacity 0.6s ease; }
.atelier.lit .night { opacity: 0.6; }
.atelier .pieces { filter: url(#shadow); }
.atelier[data-busy='1'] .pieces, .atelier.blueprint .pieces { filter: none; }
.atelier.blueprint .mat-face { fill: #0b2747; }
.atelier.blueprint .mat-grid { fill: url(#blueprint-grid); }
.atelier.blueprint .pcd * { fill: none !important; stroke: #a5d8ff !important; stroke-width: 1.15px !important; vector-effect: non-scaling-stroke; stroke-dasharray: none !important; }
.atelier.blueprint .pcd text { fill: #d6ecff !important; stroke: none !important; }
.atelier.blueprint .ghost { stroke: #d6ecff; stroke-opacity: 0.25; }
.atelier .guides { animation: atelier-fade 0.9s ease both; }
@keyframes atelier-fade { from { opacity: 0; } to { opacity: 1; } }
`
