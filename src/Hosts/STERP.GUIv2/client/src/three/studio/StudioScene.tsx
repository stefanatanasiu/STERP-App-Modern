import { CameraControls, Environment, Html, Lightformer, QuadraticBezierLine, Sparkles, useCursor } from '@react-three/drei'
import type { QuadraticBezierLineRef } from '@react-three/drei'
import { Canvas, useFrame } from '@react-three/fiber'
import { Bloom, EffectComposer, ToneMapping, Vignette } from '@react-three/postprocessing'
import { ToneMappingMode } from 'postprocessing'
import { memo, Suspense, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import * as THREE from 'three'
import { PART_COLOR, type PartKey, type StudioMaterial, type StudioModel } from '@/components/bom/studioModel'
import { cn } from '@/components/ui/primitives'
import { CakeModel } from '@/three/cake/CakeModel'
import { Prop } from '@/three/cake/Props'
import { marbleTexture } from '@/three/cake/textures'
import type { Anchors, Lighting, ProductView } from './types'

export type { Lighting }
export type StudioSelection = { kind: 'material'; item: string } | { kind: 'part'; part: PartKey }

export interface StudioSceneProps {
  model: StudioModel
  view: ProductView
  showIngredients: boolean
  showBox: boolean
  lighting: Lighting
  autoRotate: boolean
  selection: StudioSelection | null
  onSelect: (s: StudioSelection | null) => void
  /** The API figure shown under each material (already formatted). */
  figure: (m: StudioMaterial) => string
  /** The API figure shown next to a part in the exploded view. */
  partFigure: (part: PartKey) => string
  fitToken: number
}

const REDUCED_MOTION = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
const TABLE_R = 21
const PROP_SCALE = 1.3

export function StudioScene(props: StudioSceneProps) {
  return (
    <Canvas
      shadows="percentage"
      dpr={[1, 2]}
      camera={{ position: [0, 12, 28], fov: 34, near: 0.1, far: 400 }}
      gl={{ antialias: false, powerPreference: 'high-performance' }}
      onPointerMissed={() => props.onSelect(null)}
    >
      <Suspense fallback={null}>
        <Content {...props} />
      </Suspense>
    </Canvas>
  )
}

function Content({ model, view, showIngredients, showBox, lighting, autoRotate, selection, onSelect, figure, partFigure, fitToken }: StudioSceneProps) {
  const anchors = useRef<Anchors>({})
  const [hoverMaterial, setHoverMaterial] = useState<string | null>(null)
  const [hoverPart, setHoverPart] = useState<PartKey | null>(null)
  useCursor(!!hoverMaterial || !!hoverPart)
  const candle = lighting === 'candle'

  const layout = useMemo(() => ringLayout(model.materials), [model.materials])
  const byItem = useMemo(() => new Map(model.materials.map((m) => [m.item, m])), [model.materials])

  // what is in focus: a material and the parts it goes into, or a part and the materials that go into it
  const focus = useMemo(() => {
    const sel: StudioSelection | null = selection ?? (hoverMaterial ? { kind: 'material', item: hoverMaterial } : hoverPart ? { kind: 'part', part: hoverPart } : null)
    const parts = new Set<PartKey>()
    const materials = new Set<string>()
    if (sel?.kind === 'material') {
      const m = byItem.get(sel.item)
      if (m) {
        materials.add(m.item)
        m.feeds.forEach((p) => parts.add(p))
      }
    } else if (sel?.kind === 'part') {
      parts.add(sel.part)
      for (const m of model.materials) if (m.feeds.includes(sel.part)) materials.add(m.item)
    }
    return { active: !!sel, parts, materials, sel }
  }, [selection, hoverMaterial, hoverPart, byItem, model.materials])

  const links = useMemo(() => {
    if (!showIngredients || !focus.active) return []
    const out: { key: string; item: string; part: PartKey; reverse: boolean }[] = []
    for (const item of focus.materials) {
      const m = byItem.get(item)
      if (!m) continue
      for (const part of m.feeds) {
        if (focus.sel?.kind === 'part' && part !== focus.sel.part) continue
        out.push({ key: `${item}->${part}`, item, part, reverse: m.byproduct })
      }
    }
    return out
  }, [focus, byItem, showIngredients])

  return (
    <>
      <Backdrop colors={candle ? ['#07050c', '#1a0f16', '#050407'] : ['#1b1230', '#3a2440', '#0d0a12']} />
      <fog attach="fog" args={[candle ? '#0a0710' : '#140f1c', 46, 120]} />
      <Lights candle={candle} anchors={anchors} lit={model.spec.candles > 0} />
      <Environment resolution={256} frames={1}>
        <Lightformer form="rect" intensity={candle ? 0.6 : 3.2} color="#fff4e6" position={[0, 10, 2]} rotation-x={Math.PI / 2} scale={[14, 8, 1]} />
        <Lightformer form="rect" intensity={candle ? 0.3 : 1.6} color="#ffd9e6" position={[-9, 3, 4]} rotation-y={Math.PI / 2} scale={[10, 3, 1]} />
        <Lightformer form="rect" intensity={candle ? 0.3 : 1.4} color="#d6e4ff" position={[9, 3, -2]} rotation-y={-Math.PI / 2} scale={[10, 3, 1]} />
        <Lightformer form="ring" intensity={candle ? 1.2 : 2} color="#ffcf8a" position={[0, 2, -9]} scale={4} />
      </Environment>
      <Table candle={candle} />
      {model.bakery ? (
        <CakeModel
          spec={model.spec}
          view={view}
          highlight={focus.parts}
          showBox={showBox}
          lit={model.spec.candles > 0}
          anchors={anchors}
          onPart={(part) => onSelect({ kind: 'part', part })}
          onHoverPart={setHoverPart}
        />
      ) : (
        <Core anchors={anchors} active={focus.parts.has('core')} onSelect={() => onSelect({ kind: 'part', part: 'core' })} onHover={(h) => setHoverPart(h ? 'core' : null)} title={model.root.line.item} />
      )}
      {model.bakery && view === 'exploded' && <PartLabels anchors={anchors} model={model} figure={partFigure} />}

      {model.materials.map((m) => {
        const at = layout.get(m.item)!
        return (
          <PropSlot
            key={m.item}
            material={m}
            position={at.position}
            rotation={at.rotation}
            hidden={!showIngredients}
            selected={focus.sel?.kind === 'material' && focus.sel.item === m.item}
            related={focus.materials.has(m.item)}
            dimmed={focus.active && !focus.materials.has(m.item)}
            figure={figure(m)}
            onSelect={() => onSelect({ kind: 'material', item: m.item })}
            onHover={(h) => setHoverMaterial(h ? m.item : null)}
          >
            <Prop kind={m.kind} tint={m.tint} title={m.name} />
          </PropSlot>
        )
      })}

      {links.map((l) => (
        <Flow key={l.key} from={layout.get(l.item)!.position} part={l.part} reverse={l.reverse} anchors={anchors} />
      ))}

      {candle && <Sparkles count={60} scale={[16, 8, 16]} position={[0, 5, 0]} size={2.4} speed={0.2} opacity={0.5} color="#ffd08a" />}

      <Camera fitToken={fitToken} autoRotate={autoRotate && !selection} framing={view === 'exploded' ? CAKE_EXPLODED : CAKE_FRAMING} />
      <EffectComposer multisampling={4}>
        <Bloom mipmapBlur luminanceThreshold={0.92} luminanceSmoothing={0.2} intensity={candle ? 1.5 : 0.75} radius={0.75} />
        <Vignette offset={0.3} darkness={candle ? 0.85 : 0.55} />
        <ToneMapping mode={ToneMappingMode.ACES_FILMIC} />
      </EffectComposer>
    </>
  )
}

// ---------------------------------------------------------------- stage

function Backdrop({ colors }: { colors: [string, string, string] }) {
  const material = useMemo(
    () =>
      new THREE.ShaderMaterial({
        side: THREE.BackSide,
        depthWrite: false,
        fog: false,
        uniforms: {
          top: { value: new THREE.Color() },
          middle: { value: new THREE.Color() },
          bottom: { value: new THREE.Color() },
        },
        vertexShader: 'varying vec3 vP; void main(){ vP = normalize(position); gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }',
        fragmentShader:
          'uniform vec3 top; uniform vec3 middle; uniform vec3 bottom; varying vec3 vP; void main(){ float h = vP.y; vec3 c = h > 0.0 ? mix(middle, top, smoothstep(0.0, 0.7, h)) : mix(middle, bottom, smoothstep(0.0, 0.4, -h)); gl_FragColor = vec4(c, 1.0); }',
      }),
    [],
  )
  useEffect(() => {
    material.uniforms.top.value.set(colors[0])
    material.uniforms.middle.value.set(colors[1])
    material.uniforms.bottom.value.set(colors[2])
  }, [colors, material])
  return (
    <mesh material={material} scale={150}>
      <sphereGeometry args={[1, 48, 24]} />
    </mesh>
  )
}

function Table({ candle }: { candle: boolean }) {
  const marble = useMemo(() => marbleTexture(), [])
  return (
    <group>
      <mesh position-y={-0.3} receiveShadow>
        <cylinderGeometry args={[TABLE_R, TABLE_R, 0.6, 128]} />
        <meshPhysicalMaterial map={marble} roughness={0.38} clearcoat={0.45} clearcoatRoughness={0.28} color={candle ? '#b9b4be' : '#ffffff'} />
      </mesh>
      <mesh position-y={-0.02} rotation-x={Math.PI / 2}>
        <torusGeometry args={[TABLE_R, 0.06, 12, 192]} />
        <meshPhysicalMaterial color="#d6a84e" metalness={1} roughness={0.25} />
      </mesh>
    </group>
  )
}

function Lights({ candle, anchors, lit }: { candle: boolean; anchors: React.RefObject<Anchors>; lit: boolean }) {
  const glow = useRef<THREE.PointLight>(null)
  useFrame((frame) => {
    const g = glow.current
    if (!g) return
    const a = anchors.current.candles
    if (a) g.position.set(0, a.y + 1.6, 0)
    const t = frame.clock.elapsedTime
    g.intensity = candle && lit ? 26 + (REDUCED_MOTION ? 0 : Math.sin(t * 13) * 2.5 + Math.sin(t * 7.1) * 2) : 0
  })
  return (
    <>
      <ambientLight intensity={candle ? 0.07 : 0.32} />
      <spotLight
        position={[9, 20, 12]}
        angle={0.46}
        penumbra={0.9}
        intensity={candle ? 120 : 850}
        decay={2}
        color="#fff1e0"
        castShadow
        shadow-mapSize={[2048, 2048]}
        shadow-bias={-0.00015}
      />
      <directionalLight position={[-14, 10, -10]} intensity={candle ? 0.12 : 0.9} color="#c7d2fe" />
      <pointLight ref={glow} color="#ffb24d" distance={22} decay={2} intensity={0} />
    </>
  )
}

// ---------------------------------------------------------------- materials around the product

interface Slot {
  position: [number, number, number]
  rotation: number
}

/** An arc behind and beside the product, leaving the front open; materials stay together by the part they go into. */
function ringLayout(materials: StudioMaterial[]): Map<string, Slot> {
  const out = new Map<string, Slot>()
  const n = materials.length
  if (n === 0) return out
  const radius = Math.max(12.8, n * 0.5)
  const span = Math.PI * 1.58
  const groups: StudioMaterial[][] = []
  for (const m of materials) {
    const key = m.byproduct ? 'by' : m.service ? 'svc' : (m.feeds[0] ?? 'x')
    const last = groups[groups.length - 1]
    const lastKey = last ? (last[0].byproduct ? 'by' : last[0].service ? 'svc' : (last[0].feeds[0] ?? 'x')) : null
    if (last && lastKey === key) last.push(m)
    else groups.push([m])
  }
  const gap = 0.14
  const step = n > 1 ? (span - gap * (groups.length - 1)) / (n - 1) : 0
  let a = Math.PI - span / 2
  groups.forEach((g, gi) => {
    if (gi > 0) a += gap
    for (const m of g) {
      out.set(m.item, { position: [Math.sin(a) * radius, 0, Math.cos(a) * radius], rotation: a + Math.PI })
      a += step
    }
  })
  return out
}

const PropSlot = memo(function PropSlot({
  material,
  position,
  rotation,
  hidden,
  selected,
  related,
  dimmed,
  figure,
  onSelect,
  onHover,
  children,
}: {
  material: StudioMaterial
  position: [number, number, number]
  rotation: number
  /** kept mounted while hidden: unmounting many HTML captions at once is what the overlay library handles worst */
  hidden: boolean
  selected: boolean
  related: boolean
  dimmed: boolean
  figure: string
  onSelect: () => void
  onHover: (h: boolean) => void
  children: ReactNode
}) {
  const group = useRef<THREE.Group>(null)
  const ring = useRef<THREE.MeshBasicMaterial>(null)
  const label = useRef<HTMLDivElement>(null)
  const world = useMemo(() => new THREE.Vector3(position[0], position[1] + 2.4, position[2]), [position])
  const [hovered, setHovered] = useState(false)
  const color = material.byproduct ? '#fbbf24' : material.service ? '#34d399' : PART_COLOR[material.feeds[0] ?? 'core']

  useFrame((frame, dt) => {
    const g = group.current
    if (!g) return
    const lift = selected ? 0.35 + (REDUCED_MOTION ? 0 : Math.sin(frame.clock.elapsedTime * 2.4) * 0.08) : hovered ? 0.18 : 0
    g.position.y = THREE.MathUtils.damp(g.position.y, lift + 0.1, 8, dt)
    const s = THREE.MathUtils.damp(g.scale.x, (selected ? 1.12 : hovered ? 1.06 : 1) * PROP_SCALE, 8, dt)
    g.scale.setScalar(s)
    if (ring.current) ring.current.opacity = THREE.MathUtils.damp(ring.current.opacity, selected || related ? 0.95 : hovered ? 0.6 : dimmed ? 0.06 : 0.22, 8, dt)
    if (label.current) {
      // captions on the near side of the table only (or whatever is in focus), so the far side never piles up
      const d = frame.camera.position.distanceTo(world)
      const near = d < frame.camera.position.length() + 1.5
      const show = !hidden && (selected || hovered || related || (near && !dimmed))
      const k = THREE.MathUtils.clamp(30 / d, 0.66, 1.05)
      const current = Number(label.current.style.opacity || '0')
      label.current.style.opacity = THREE.MathUtils.damp(current, show ? 1 : 0, 10, dt).toFixed(3)
      label.current.style.transform = `scale(${k.toFixed(3)})`
    }
  })

  return (
    <group position={position} visible={!hidden}>
      <mesh position-y={0.02} receiveShadow>
        <cylinderGeometry args={[0.95, 0.95, 0.04, 48]} />
        <meshPhysicalMaterial color="#ffffff" roughness={0.1} transmission={0.6} thickness={0.1} transparent opacity={0.55} />
      </mesh>
      <mesh position-y={0.05} rotation-x={Math.PI / 2}>
        <torusGeometry args={[0.96, 0.025, 8, 64]} />
        <meshBasicMaterial ref={ring} color={new THREE.Color(color).multiplyScalar(2.4)} transparent opacity={0.22} toneMapped={false} />
      </mesh>
      <group
        ref={group}
        rotation-y={rotation}
        onClick={(e) => {
          if (hidden) return
          e.stopPropagation()
          onSelect()
        }}
        onPointerOver={(e) => {
          if (hidden) return
          e.stopPropagation()
          setHovered(true)
          onHover(true)
        }}
        onPointerOut={() => {
          setHovered(false)
          onHover(false)
        }}
      >
        {children}
      </group>
      <Html position={[0, 2.5, 0]} center zIndexRange={[10, 0]} style={{ pointerEvents: 'none' }}>
        <div ref={label} className="origin-bottom" style={{ opacity: 0 }}>
          <div
            className={cn(
              'flex flex-col items-center rounded-xl border px-2.5 py-1.5 whitespace-nowrap backdrop-blur-md',
              selected ? 'border-white/30 bg-ink-900/90 shadow-[0_10px_30px_-10px_rgba(0,0,0,0.9)]' : 'border-white/10 bg-ink-900/70',
            )}
          >
            <span className="text-[11.5px] font-semibold text-ink-50">{material.name}</span>
            <span className="flex items-center gap-1.5 text-[10.5px] text-ink-300 tabular">
              <span className="size-1.5 rounded-full" style={{ background: color, boxShadow: `0 0 8px ${color}` }} />
              {figure}
            </span>
          </div>
        </div>
      </Html>
    </group>
  )
})

// ---------------------------------------------------------------- flows

function Flow({ from, part, reverse, anchors }: { from: [number, number, number]; part: PartKey; reverse: boolean; anchors: React.RefObject<Anchors> }) {
  const line = useRef<QuadraticBezierLineRef>(null)
  const dots = useRef<(THREE.Mesh | null)[]>([])
  const v = useMemo(() => ({ a: new THREE.Vector3(...from).setY(from[1] + 1.7), b: new THREE.Vector3(), m: new THREE.Vector3(), curve: new THREE.QuadraticBezierCurve3() }), [from])
  const color = PART_COLOR[part]
  useFrame((frame, dt) => {
    const target = anchors.current[part]
    const l = line.current
    if (!target || !l) return
    v.b.copy(target)
    v.m.copy(v.a).add(v.b).multiplyScalar(0.5)
    v.m.y = Math.max(v.a.y, v.b.y) + 5.5
    l.setPoints(v.a, v.b, v.m)
    if (reverse) {
      v.curve.v0.copy(v.b)
      v.curve.v2.copy(v.a)
    } else {
      v.curve.v0.copy(v.a)
      v.curve.v2.copy(v.b)
    }
    v.curve.v1.copy(v.m)
    const mat = l.material as THREE.Material & { dashOffset?: number }
    if (mat.dashOffset !== undefined && !REDUCED_MOTION) mat.dashOffset -= dt * (reverse ? -1.4 : 1.4)
    dots.current.forEach((d, i) => {
      if (!d) return
      const t = (frame.clock.elapsedTime * 0.28 + i / 4) % 1
      v.curve.getPoint(t, d.position)
      d.scale.setScalar(0.09 + Math.sin(t * Math.PI) * 0.06)
    })
  })
  return (
    <>
      <QuadraticBezierLine ref={line} start={[0, 0, 0]} end={[0, 0, 0]} color={color} lineWidth={2.4} dashed dashSize={0.5} gapSize={0.25} transparent opacity={0.9} toneMapped={false} />
      {!REDUCED_MOTION &&
        [0, 1, 2, 3].map((i) => (
          <mesh key={i} ref={(d) => void (dots.current[i] = d)}>
            <sphereGeometry args={[1, 12, 8]} />
            <meshBasicMaterial color={new THREE.Color(color).multiplyScalar(3)} toneMapped={false} />
          </mesh>
        ))}
    </>
  )
}

// ---------------------------------------------------------------- exploded view captions

function PartLabels({ anchors, model, figure }: { anchors: React.RefObject<Anchors>; model: StudioModel; figure: (p: PartKey) => string }) {
  const keys = (['sponge', 'buttercream', 'jam', 'fondant', 'roses', 'topper', 'candles'] as PartKey[]).filter((k) => model.parts.has(k))
  const refs = useRef<(THREE.Group | null)[]>([])
  useFrame(() => {
    keys.forEach((k, i) => {
      const a = anchors.current[k]
      const g = refs.current[i]
      if (a && g) g.position.set(5.6, a.y, 0)
    })
  })
  return (
    <>
      {keys.map((k, i) => (
        <group key={k} ref={(g) => void (refs.current[i] = g)}>
          <Html center zIndexRange={[9, 0]} style={{ pointerEvents: 'none' }}>
            <div className="flex -translate-x-2 items-center gap-2 whitespace-nowrap">
              <span className="h-px w-10 bg-[linear-gradient(90deg,transparent,rgba(255,255,255,0.7))]" />
              <span className="rounded-lg border border-white/15 bg-ink-900/80 px-2 py-1 text-[11px] text-ink-100 backdrop-blur">
                <span className="mr-1.5 inline-block size-1.5 rounded-full" style={{ background: PART_COLOR[k] }} />
                {model.parts.get(k)!.name}
                <span className="ml-2 text-ink-400 tabular">{figure(k)}</span>
              </span>
            </div>
          </Html>
        </group>
      ))}
    </>
  )
}

// ---------------------------------------------------------------- products that are not cakes

function Core({ anchors, active, onSelect, onHover, title }: { anchors: React.RefObject<Anchors>; active: boolean; onSelect: () => void; onHover: (h: boolean) => void; title: string }) {
  const gem = useRef<THREE.Mesh>(null)
  const rings = useRef<THREE.Group>(null)
  useEffect(() => {
    anchors.current.core = new THREE.Vector3(0, 3.6, 0)
  }, [anchors])
  useFrame((frame, dt) => {
    if (gem.current) {
      gem.current.rotation.y += dt * 0.35
      gem.current.position.y = 3.6 + Math.sin(frame.clock.elapsedTime * 1.2) * 0.15
    }
    if (rings.current) rings.current.rotation.z += dt * 0.2
  })
  return (
    <group>
      <mesh position-y={0.6} castShadow receiveShadow>
        <cylinderGeometry args={[2.6, 3, 1.2, 96]} />
        <meshPhysicalMaterial color="#e5e7eb" metalness={1} roughness={0.25} />
      </mesh>
      <mesh
        ref={gem}
        castShadow
        onClick={(e) => {
          e.stopPropagation()
          onSelect()
        }}
        onPointerOver={(e) => {
          e.stopPropagation()
          onHover(true)
        }}
        onPointerOut={() => onHover(false)}
      >
        <icosahedronGeometry args={[1.7, 0]} />
        <meshPhysicalMaterial color="#c4b5fd" roughness={0.06} transmission={0.7} thickness={1.4} iridescence={1} iridescenceIOR={1.4} emissive="#7c3aed" emissiveIntensity={active ? 0.6 : 0.15} />
      </mesh>
      <group ref={rings} position-y={3.6} rotation-x={Math.PI / 2.3}>
        <mesh>
          <torusGeometry args={[2.5, 0.025, 8, 128]} />
          <meshBasicMaterial color={new THREE.Color('#a78bfa').multiplyScalar(2)} toneMapped={false} />
        </mesh>
      </group>
      <Html position={[0, 6.1, 0]} center style={{ pointerEvents: 'none' }}>
        <div className="rounded-full border border-white/20 bg-ink-900/80 px-3 py-1 font-mono text-[12px] text-ink-50 backdrop-blur">{title}</div>
      </Html>
    </group>
  )
}

// ---------------------------------------------------------------- camera

/** Camera position and target. */
type Framing = readonly [number, number, number, number, number, number]
const CAKE_FRAMING: Framing = [0, 18, 39, 0, 1.8, 0]
const CAKE_EXPLODED: Framing = [0, 19, 41, 0, 4.8, 0]

function Camera({ fitToken, autoRotate, framing }: { fitToken: number; autoRotate: boolean; framing: Framing }) {
  const controls = useRef<CameraControls>(null)
  const activity = useRef(0)
  const [px, py, pz, tx, ty, tz] = framing
  useEffect(() => {
    const c = controls.current
    if (!c) return
    activity.current = performance.now()
    void c.setLookAt(px, py, pz, tx, ty, tz, true)
  }, [fitToken, px, py, pz, tx, ty, tz])
  useFrame((_, dt) => {
    if (autoRotate && !REDUCED_MOTION && performance.now() - activity.current > 3000) controls.current?.rotate(dt * 0.06, 0, false)
  })
  return (
    <CameraControls
      ref={controls}
      makeDefault
      minDistance={7}
      maxDistance={70}
      minPolarAngle={0.2}
      maxPolarAngle={1.45}
      smoothTime={0.6}
      draggingSmoothTime={0.12}
      onControlStart={() => (activity.current = performance.now())}
      onControl={() => (activity.current = performance.now())}
    />
  )
}
