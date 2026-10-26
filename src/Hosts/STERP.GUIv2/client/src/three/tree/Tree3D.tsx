import { CameraControls, Environment, Grid, Html, Lightformer, QuadraticBezierLine, Sparkles, Stars, useCursor } from '@react-three/drei'
import type { QuadraticBezierLineRef } from '@react-three/drei'
import { Canvas, useFrame, type ThreeEvent } from '@react-three/fiber'
import { Bloom, EffectComposer, ToneMapping, Vignette } from '@react-three/postprocessing'
import { ToneMappingMode } from 'postprocessing'
import { createContext, memo, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type Ref } from 'react'
import * as THREE from 'three'
import { CLASS_META, type NodeClass } from '@/domain/kinds'
import { cn } from '@/components/ui/primitives'
import { layoutTree, type LayoutKind, type LayoutResult, type Vec3 } from './layout'
import { flatten, type VizNode } from './viz'

export type LabelMode = 'auto' | 'all' | 'none'

export interface Tree3DProps {
  root: VizNode
  layout: LayoutKind
  collapsed: Set<string>
  selectedId: string | null
  onSelect: (id: string | null) => void
  onToggle: (id: string) => void
  labels: LabelMode
  autoRotate: boolean
  matches?: Set<string>
  /** Grow upwards (where used). */
  upwards?: boolean
  /** Change to refit the camera to the whole tree. */
  fitToken?: number
  className?: string
}

const REDUCED_MOTION = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

// ---------------------------------------------------------------- shared geometry and looks

const GEO = {
  sphere: new THREE.SphereGeometry(1, 48, 32),
  ico: new THREE.IcosahedronGeometry(1, 1),
  icoFine: new THREE.IcosahedronGeometry(1, 4),
  capsule: new THREE.CapsuleGeometry(0.62, 0.75, 10, 24),
  torus: new THREE.TorusGeometry(0.78, 0.3, 24, 72),
  octa: new THREE.OctahedronGeometry(1, 0),
  ring: new THREE.TorusGeometry(1, 0.03, 12, 128),
  particle: new THREE.SphereGeometry(1, 12, 8),
}

interface Look {
  geometry: THREE.BufferGeometry
  color: string
  emissive: string
  radius: number
  shell?: boolean
  wire?: boolean
}

const LOOK: Record<NodeClass, Look> = {
  root: { geometry: GEO.icoFine, color: '#fdf2f8', emissive: '#ec4899', radius: 1.3, shell: true },
  made: { geometry: GEO.sphere, color: '#ddd6fe', emissive: '#8b5cf6', radius: 0.8, shell: true },
  bought: { geometry: GEO.sphere, color: '#cffafe', emissive: '#06b6d4', radius: 0.55 },
  service: { geometry: GEO.capsule, color: '#d1fae5', emissive: '#10b981', radius: 0.5 },
  phantom: { geometry: GEO.ico, color: '#f1f5f9', emissive: '#94a3b8', radius: 0.72, wire: true },
  byproduct: { geometry: GEO.torus, color: '#fef3c7', emissive: '#f59e0b', radius: 0.62 },
  note: { geometry: GEO.octa, color: '#e2e8f0', emissive: '#64748b', radius: 0.4 },
}

function radiusOf(n: VizNode): number {
  const base = LOOK[n.cls].radius
  // product-class nodes away from the root (tops of where-used chains) stay smaller than the subject of the tree
  if (n.cls === 'root' && n.depth > 0) return 0.95
  if (n.weight === undefined || n.cls === 'root') return base
  return 0.34 + 1.05 * Math.sqrt(Math.max(0, Math.min(1, n.weight)))
}

const easeInOutCubic = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2)

// ---------------------------------------------------------------- layout animation

/** Interpolates every node from where it is now to its new place when the layout or the visible set changes. */
class Animator {
  from = new Map<string, Vec3>()
  to = new Map<string, Vec3>()
  t = 1
  version = 0

  retarget(next: Map<string, Vec3>, parentOf: (id: string) => string | null) {
    const from = new Map<string, Vec3>()
    for (const [id, target] of next) {
      let start = this.at(id)
      let p = parentOf(id)
      while (!start && p) {
        start = this.at(p)
        p = parentOf(p)
      }
      from.set(id, start ?? target)
    }
    this.from = from
    this.to = next
    this.t = REDUCED_MOTION ? 1 : 0
    this.version++
  }

  at(id: string): Vec3 | undefined {
    const b = this.to.get(id)
    if (!b) return undefined
    const a = this.from.get(id) ?? b
    const k = easeInOutCubic(this.t)
    return [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k]
  }

  write(id: string, v: THREE.Vector3): boolean {
    const p = this.at(id)
    if (!p) return false
    v.set(p[0], p[1], p[2])
    return true
  }

  tick(dt: number) {
    if (this.t < 1) this.t = Math.min(1, this.t + dt / 1.05)
  }
}

const AnimatorContext = createContext<Animator>(new Animator())

function AnimatorTicker() {
  const animator = useContext(AnimatorContext)
  useFrame((_, dt) => animator.tick(dt), -1)
  return null
}

// ---------------------------------------------------------------- nodes

type NodeState = 'normal' | 'dim' | 'focus' | 'selected'

interface NodeProps {
  node: VizNode
  state: NodeState
  hovered: boolean
  match: boolean
  showLabel: boolean
  hiddenChildren: number
  onHover: (id: string | null) => void
  onSelect: (id: string) => void
  onToggle: (id: string) => void
}

const Node = memo(function Node({ node, state, hovered, match, showLabel, hiddenChildren, onHover, onSelect, onToggle }: NodeProps) {
  const animator = useContext(AnimatorContext)
  const group = useRef<THREE.Group>(null)
  const body = useRef<THREE.Mesh>(null)
  const material = useRef<THREE.MeshPhysicalMaterial>(null)
  const shell = useRef<THREE.MeshBasicMaterial>(null)
  const label = useRef<HTMLDivElement>(null)
  const look = LOOK[node.cls]
  const r = radiusOf(node)
  useCursor(hovered)

  useFrame((frame, dt) => {
    if (!group.current) return
    animator.write(node.id, group.current.position)
    // labels keep a readable size: they shrink with distance, but within bounds
    if (label.current) {
      const s = THREE.MathUtils.clamp(26 / frame.camera.position.distanceTo(group.current.position), 0.62, 1.08)
      label.current.style.transform = `translateY(-50%) scale(${s.toFixed(3)})`
    }
    const pulse = match ? 0.5 + 0.5 * Math.sin(frame.clock.elapsedTime * 4) : 0
    const targetScale = r * (state === 'selected' ? 1.18 : hovered ? 1.12 : 1)
    if (body.current) {
      const s = THREE.MathUtils.damp(body.current.scale.x, targetScale, 9, dt)
      body.current.scale.setScalar(s)
      if (node.cls === 'root' || node.cls === 'phantom' || node.cls === 'byproduct') body.current.rotation.y += dt * (node.cls === 'root' ? 0.25 : 0.5)
    }
    if (material.current) {
      const glow = state === 'selected' ? 2.4 : hovered ? 1.6 : state === 'focus' ? 0.95 : state === 'dim' ? 0.06 : 0.55
      material.current.emissiveIntensity = THREE.MathUtils.damp(material.current.emissiveIntensity, glow + pulse * 1.4, 8, dt)
      material.current.opacity = THREE.MathUtils.damp(material.current.opacity, state === 'dim' ? 0.22 : 1, 8, dt)
    }
    if (shell.current) shell.current.opacity = THREE.MathUtils.damp(shell.current.opacity, state === 'dim' ? 0.02 : 0.1 + (hovered ? 0.08 : 0), 8, dt)
  })

  const stop = (e: ThreeEvent<PointerEvent | MouseEvent>) => e.stopPropagation()

  return (
    <group ref={group}>
      <mesh
        ref={body}
        geometry={look.geometry}
        scale={r}
        onPointerOver={(e) => {
          stop(e)
          onHover(node.id)
        }}
        onPointerOut={() => onHover(null)}
        onClick={(e) => {
          stop(e)
          onSelect(node.id)
        }}
        onDoubleClick={(e) => {
          stop(e)
          onToggle(node.id)
        }}
      >
        <meshPhysicalMaterial
          ref={material}
          color={look.color}
          emissive={look.emissive}
          emissiveIntensity={0.55}
          roughness={look.wire ? 0.6 : 0.18}
          metalness={node.cls === 'root' ? 0.35 : 0.08}
          clearcoat={1}
          clearcoatRoughness={0.12}
          transparent
          wireframe={look.wire}
        />
      </mesh>
      {look.shell && (
        <mesh geometry={GEO.sphere} scale={r * 1.45}>
          <meshBasicMaterial ref={shell} color={look.emissive} transparent opacity={0.11} depthWrite={false} blending={THREE.AdditiveBlending} />
        </mesh>
      )}
      {state === 'selected' && <SelectionRing radius={r} color={CLASS_META[node.cls].color} />}
      {node.ring && <StatusRing radius={r} kind={node.ring} dim={state === 'dim'} />}
      {hiddenChildren > 0 && (
        <mesh geometry={GEO.ring} scale={r * 1.9} rotation={[Math.PI / 2, 0, 0]}>
          <meshBasicMaterial color="#e9d5ff" transparent opacity={state === 'dim' ? 0.05 : 0.45} toneMapped={false} />
        </mesh>
      )}
      {showLabel && (
        <Html position={[0, r + 0.5, 0]} center zIndexRange={[8, 0]} style={{ pointerEvents: 'none' }}>
          <NodeLabel ref={label} node={node} state={state} hovered={hovered} hiddenChildren={hiddenChildren} />
        </Html>
      )}
    </group>
  )
})

function NodeLabel({
  ref,
  node,
  state,
  hovered,
  hiddenChildren,
}: {
  ref: Ref<HTMLDivElement>
  node: VizNode
  state: NodeState
  hovered: boolean
  hiddenChildren: number
}) {
  const meta = CLASS_META[node.cls]
  const big = node.cls === 'root' || state === 'selected' || hovered
  return (
    <div
      ref={ref}
      className={cn(
        'flex origin-bottom flex-col items-center gap-0.5 whitespace-nowrap transition-opacity duration-300 select-none',
        state === 'dim' ? 'opacity-20' : 'opacity-100',
      )}
    >
      <div
        className={cn(
          'flex items-center gap-1.5 rounded-full border px-2.5 py-1 backdrop-blur-md',
          big ? 'border-white/25 bg-ink-900/85 shadow-[0_8px_30px_-8px_rgba(0,0,0,0.8)]' : 'border-white/10 bg-ink-900/65',
        )}
      >
        <span className="size-1.5 rounded-full" style={{ background: meta.color, boxShadow: `0 0 10px ${meta.color}` }} />
        <span className={cn('font-mono font-medium tracking-tight text-ink-50', big ? 'text-[12px]' : 'text-[10.5px]')}>{node.code}</span>
        {node.figure && <span className={cn('font-medium text-ink-300 tabular', big ? 'text-[11px]' : 'text-[10px]')}>{node.figure}</span>}
        {hiddenChildren > 0 && <span className="rounded-full bg-violet-400/25 px-1.5 text-[9.5px] font-semibold text-violet-100">+{hiddenChildren}</span>}
      </div>
      {big && node.title && <div className="max-w-[240px] truncate rounded-md bg-ink-950/70 px-2 py-0.5 text-[10.5px] text-ink-200">{node.title}</div>}
    </div>
  )
}

function SelectionRing({ radius, color }: { radius: number; color: string }) {
  const a = useRef<THREE.Mesh>(null)
  const b = useRef<THREE.Mesh>(null)
  useFrame((_, dt) => {
    if (a.current) a.current.rotation.z += dt * 0.9
    if (b.current) b.current.rotation.x += dt * 0.6
  })
  const c = new THREE.Color(color).multiplyScalar(2.2)
  return (
    <>
      <mesh ref={a} geometry={GEO.ring} scale={radius * 1.75} rotation={[Math.PI / 2.4, 0, 0]}>
        <meshBasicMaterial color={c} toneMapped={false} />
      </mesh>
      <mesh ref={b} geometry={GEO.ring} scale={radius * 2.05} rotation={[0, Math.PI / 3, 0]}>
        <meshBasicMaterial color={c} transparent opacity={0.55} toneMapped={false} />
      </mesh>
    </>
  )
}

function StatusRing({ radius, kind, dim }: { radius: number; kind: 'shortage' | 'covered'; dim: boolean }) {
  const ref = useRef<THREE.Mesh>(null)
  useFrame((clock) => {
    if (!ref.current) return
    const s = kind === 'shortage' && !REDUCED_MOTION ? 1.5 + 0.25 * Math.sin(clock.clock.elapsedTime * 3) : 1.5
    ref.current.scale.setScalar(radius * s)
  })
  return (
    <mesh ref={ref} geometry={GEO.ring} rotation={[Math.PI / 2, 0, 0]}>
      <meshBasicMaterial
        color={new THREE.Color(kind === 'shortage' ? '#fb7185' : '#34d399').multiplyScalar(dim ? 0.3 : 2)}
        transparent
        opacity={dim ? 0.2 : 0.95}
        toneMapped={false}
      />
    </mesh>
  )
}

// ---------------------------------------------------------------- edges

interface EdgeProps {
  parentId: string
  childId: string
  color: string
  active: boolean
  dim: boolean
  arc: boolean
}

const Edge = memo(function Edge({ parentId, childId, color, active, dim, arc }: EdgeProps) {
  const animator = useContext(AnimatorContext)
  const line = useRef<QuadraticBezierLineRef>(null)
  const particles = useRef<(THREE.Mesh | null)[]>([])
  const seen = useRef({ version: -1, settled: false })
  const v = useMemo(() => ({ p: new THREE.Vector3(), c: new THREE.Vector3(), m: new THREE.Vector3(), curve: new THREE.QuadraticBezierCurve3() }), [])

  useFrame((clock, dt) => {
    const l = line.current
    if (!l) return
    const moving = animator.t < 1
    if (moving || seen.current.version !== animator.version || !seen.current.settled) {
      if (!animator.write(parentId, v.p) || !animator.write(childId, v.c)) return
      control(v.p, v.c, v.m, arc)
      l.setPoints(v.p, v.c, v.m)
      v.curve.v0.copy(v.c)
      v.curve.v1.copy(v.m)
      v.curve.v2.copy(v.p)
      seen.current = { version: animator.version, settled: !moving }
    }
    const mat = l.material as THREE.Material & { dashOffset?: number; opacity: number }
    if (active && mat.dashOffset !== undefined && !REDUCED_MOTION) mat.dashOffset -= dt * 1.6
    mat.opacity = THREE.MathUtils.damp(mat.opacity, dim ? 0.06 : active ? 0.95 : 0.38, 8, dt)
    if (active) {
      particles.current.forEach((mesh, i) => {
        if (!mesh) return
        const t = (clock.clock.elapsedTime * 0.32 + i / 3) % 1
        v.curve.getPoint(t, mesh.position)
      })
    }
  })

  return (
    <>
      <QuadraticBezierLine
        ref={line}
        start={[0, 0, 0]}
        end={[0, 0, 0]}
        color={color}
        lineWidth={active ? 2.8 : 1.4}
        transparent
        opacity={0.38}
        dashed={active}
        dashSize={0.55}
        gapSize={0.3}
        depthWrite={false}
        toneMapped={!active}
      />
      {active &&
        !REDUCED_MOTION &&
        [0, 1, 2].map((i) => (
          <mesh key={i} ref={(m) => void (particles.current[i] = m)} geometry={GEO.particle} scale={0.11}>
            <meshBasicMaterial color={new THREE.Color(color).multiplyScalar(3)} toneMapped={false} />
          </mesh>
        ))}
    </>
  )
})

function control(p: THREE.Vector3, c: THREE.Vector3, out: THREE.Vector3, arc: boolean) {
  if (arc) {
    out.copy(p).add(c).multiplyScalar(0.5)
    out.y += p.distanceTo(c) * 0.22
    return
  }
  // hangs from the parent: leave horizontally towards the child's column, then drop
  out.set(c.x * 0.65 + p.x * 0.35, p.y * 0.8 + c.y * 0.2, c.z * 0.65 + p.z * 0.35)
}

// ---------------------------------------------------------------- camera

function CameraRig({
  layout,
  kind,
  focus,
  fitToken,
  autoRotate,
}: {
  layout: LayoutResult
  kind: LayoutKind
  focus: Vec3 | null
  fitToken: number
  autoRotate: boolean
}) {
  const controls = useRef<CameraControls>(null)
  const activity = useRef(0)
  const { center, radius } = layout

  useEffect(() => {
    const c = controls.current
    if (!c) return
    activity.current = performance.now()
    // the orbit reads best from above, cone and tiers from just above the horizon
    void c.rotatePolarTo(kind === 'orbit' ? 0.82 : 1.22, true)
    void c.fitToSphere(new THREE.Sphere(new THREE.Vector3(...center), radius * 0.92), true)
  }, [fitToken, kind, center[0], center[1], center[2], radius]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    const c = controls.current
    if (!c || !focus) return
    activity.current = performance.now()
    const target = new THREE.Vector3(...focus)
    const pos = c.getPosition(new THREE.Vector3())
    const dir = pos.sub(c.getTarget(new THREE.Vector3())).normalize()
    const dest = target.clone().add(dir.multiplyScalar(Math.max(18, radius * 0.5)))
    void c.setLookAt(dest.x, dest.y, dest.z, target.x, target.y, target.z, true)
  }, [focus]) // eslint-disable-line react-hooks/exhaustive-deps

  useFrame((_, dt) => {
    if (autoRotate && !REDUCED_MOTION && performance.now() - activity.current > 2500) controls.current?.rotate(dt * 0.07, 0, false)
  })

  return (
    <CameraControls
      ref={controls}
      makeDefault
      smoothTime={0.55}
      draggingSmoothTime={0.12}
      minDistance={2.5}
      maxDistance={400}
      dollySpeed={0.7}
      onControlStart={() => (activity.current = performance.now())}
      onControl={() => (activity.current = performance.now())}
    />
  )
}

// ---------------------------------------------------------------- scene

function Scene(props: Tree3DProps & { hovered: string | null; setHovered: (id: string | null) => void }) {
  const { root, layout: kind, collapsed, selectedId, onSelect, onToggle, labels, autoRotate, matches, upwards, fitToken = 0, hovered, setHovered } = props
  const animator = useMemo(() => new Animator(), [])
  const all = useMemo(() => flatten(root), [root])
  const byId = useMemo(() => new Map(all.map((n) => [n.id, n])), [all])
  const layout = useMemo(() => layoutTree(root, kind, collapsed, upwards ? -1 : 1), [root, kind, collapsed, upwards])
  useLayoutEffect(() => {
    animator.retarget(layout.positions, (id) => byId.get(id)?.parent?.id ?? null)
  }, [layout, animator, byId])

  const visible = useMemo(() => all.filter((n) => layout.positions.has(n.id)), [all, layout])

  // focus: the selected (or hovered) occurrence with its ancestry and its branch
  const focusId = hovered ?? selectedId
  const focusSet = useMemo(() => {
    if (!focusId) return null
    const n = byId.get(focusId)
    if (!n) return null
    const s = new Set<string>([n.id])
    for (let p = n.parent; p; p = p.parent) s.add(p.id)
    const walk = (x: VizNode) => x.children.forEach((c) => (s.add(c.id), walk(c)))
    walk(n)
    return s
  }, [focusId, byId])
  const pathSet = useMemo(() => {
    if (!focusId) return null
    const s = new Set<string>()
    for (let n = byId.get(focusId) ?? null; n; n = n.parent) s.add(n.id)
    return s
  }, [focusId, byId])

  const focusPos = selectedId ? (layout.positions.get(selectedId) ?? null) : null
  const fog = Math.max(30, layout.radius)

  return (
    <AnimatorContext.Provider value={animator}>
      <AnimatorTicker />
      <color attach="background" args={['#05070d']} />
      <fog attach="fog" args={['#05070d', fog * 1.6, fog * 4.6]} />
      <ambientLight intensity={0.4} />
      <directionalLight position={[12, 20, 14]} intensity={1.4} color="#e0e7ff" />
      <pointLight position={[0, 10, 0]} intensity={120} distance={90} decay={2} color="#c4b5fd" />
      <pointLight position={[-18, -6, 10]} intensity={90} distance={90} decay={2} color="#22d3ee" />
      <Environment resolution={256} frames={1}>
        <Lightformer form="rect" intensity={2.4} color="#c4b5fd" position={[0, 6, -10]} scale={[14, 6, 1]} />
        <Lightformer form="ring" intensity={3} color="#67e8f9" position={[-8, 2, 6]} scale={5} />
        <Lightformer form="circle" intensity={2.2} color="#f9a8d4" position={[8, -2, 5]} scale={4} />
      </Environment>
      <Stars radius={180} depth={80} count={2600} factor={3.2} saturation={0} fade speed={0.5} />
      <Sparkles count={90} scale={[layout.radius * 2, layout.radius * 1.2, layout.radius * 2]} position={layout.center} size={2.2} speed={0.25} opacity={0.35} color="#c4b5fd" />
      <Grid
        position={[layout.center[0], (upwards ? layout.center[1] - layout.radius : layout.minY) - 3.5, layout.center[2]]}
        args={[10, 10]}
        infiniteGrid
        cellSize={1.2}
        cellThickness={0.6}
        cellColor="#1c2140"
        sectionSize={6}
        sectionThickness={1.1}
        sectionColor="#4c2f8f"
        fadeDistance={fog * 4}
        fadeStrength={2.2}
        followCamera={false}
      />

      {visible.map((n) =>
        n.parent && layout.positions.has(n.parent.id) ? (
          <Edge
            key={`e-${n.id}`}
            parentId={n.parent.id}
            childId={n.id}
            color={CLASS_META[n.cls].color}
            active={!!pathSet && pathSet.has(n.id) && pathSet.has(n.parent.id)}
            dim={!!focusSet && !focusSet.has(n.id)}
            arc={kind !== 'cone'}
          />
        ) : null,
      )}
      {visible.map((n) => {
        const state: NodeState = n.id === selectedId ? 'selected' : focusSet ? (focusSet.has(n.id) ? 'focus' : 'dim') : 'normal'
        const hiddenChildren = collapsed.has(n.id) ? n.children.length : 0
        const match = !!matches?.has(n.id)
        // automatic labels thin out as the tree grows: always the product, branches and whatever is in focus
        const crowded = visible.length > 45
        const branch = n.cls === 'made' || n.cls === 'phantom'
        const auto =
          n.depth === 0 ||
          hiddenChildren > 0 ||
          (focusSet?.has(n.id) ?? false) ||
          (!crowded && (n.depth <= 1 || branch)) ||
          (crowded && branch && n.depth <= 1)
        const showLabel = labels === 'all' || n.id === hovered || n.id === selectedId || match || (labels === 'auto' && auto)
        return (
          <Node
            key={n.id}
            node={n}
            state={state}
            hovered={hovered === n.id}
            match={match}
            showLabel={showLabel}
            hiddenChildren={hiddenChildren}
            onHover={setHovered}
            onSelect={onSelect}
            onToggle={onToggle}
          />
        )
      })}

      <CameraRig layout={layout} kind={kind} focus={focusPos} fitToken={fitToken} autoRotate={autoRotate} />
      <EffectComposer multisampling={4}>
        <Bloom mipmapBlur luminanceThreshold={0.85} luminanceSmoothing={0.25} intensity={1.05} radius={0.72} />
        <Vignette offset={0.28} darkness={0.7} />
        <ToneMapping mode={ToneMappingMode.ACES_FILMIC} />
      </EffectComposer>
    </AnimatorContext.Provider>
  )
}

export function Tree3D(props: Tree3DProps) {
  const [hovered, setHovered] = useState<string | null>(null)
  return (
    <div className={cn('relative h-full w-full', props.className)}>
      <Canvas
        dpr={[1, 2]}
        camera={{ position: [0, 8, 34], fov: 42, near: 0.1, far: 900 }}
        gl={{ antialias: false, powerPreference: 'high-performance' }}
        onPointerMissed={() => props.onSelect(null)}
      >
        <Scene {...props} hovered={hovered} setHovered={setHovered} />
      </Canvas>
    </div>
  )
}
