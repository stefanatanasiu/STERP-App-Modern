import { Edges, Text } from '@react-three/drei'
import { useFrame, type ThreeEvent } from '@react-three/fiber'
import { Suspense, useLayoutEffect, useMemo, useRef, type ReactNode, type RefObject } from 'react'
import * as THREE from 'three'
import pacifico from '@fontsource/pacifico/files/pacifico-latin-400-normal.woff?url'
import { PART_COLOR, type CakeSpec, type PartKey } from '@/components/bom/studioModel'
import type { Anchors, ProductView } from '@/three/studio/types'
import { candleTexture, crumbTexture, crustTexture, paperTexture } from './textures'

// The celebration cake, modelled procedurally from its bill: an 8 inch cake on a 10 inch drum (1 unit = 1 inch).

export type CakeView = ProductView

export const CAKE_R = 4
const DRUM_R = 5
const DRUM_H = 0.18
const SPONGE_H = 0.95
const JAM_H = 0.07
const CREAM_H = 0.17
const COAT = 0.035
const FON_T = 0.12
const EDGE = 0.34
const CUT = Math.PI / 3.1
const EXPLODE_GAP = 0.62

const REDUCED_MOTION = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

export type { Anchors }

interface CakeModelProps {
  spec: CakeSpec
  view: CakeView
  highlight: Set<PartKey>
  showBox: boolean
  lit: boolean
  anchors: RefObject<Anchors>
  onPart: (part: PartKey) => void
  onHoverPart: (part: PartKey | null) => void
}

interface StackItem {
  kind: 'sponge' | 'jam' | 'buttercream'
  y: number
  h: number
  index: number
}

function buildStack(spec: CakeSpec): { items: StackItem[]; top: number } {
  const items: StackItem[] = []
  let y = DRUM_H
  for (let i = 0; i < spec.layers; i++) {
    items.push({ kind: 'sponge', y, h: SPONGE_H, index: items.length })
    y += SPONGE_H
    if (i < spec.layers - 1) {
      if (spec.has.jam) {
        items.push({ kind: 'jam', y, h: JAM_H, index: items.length })
        y += JAM_H
      }
      if (spec.has.buttercream) {
        items.push({ kind: 'buttercream', y, h: CREAM_H, index: items.length })
        y += CREAM_H
      }
    }
  }
  return { items, top: y + COAT }
}

function useCakeMaterials() {
  return useMemo(() => {
    const crumb = crumbTexture()
    const crust = crustTexture()
    const m = {
      spongeSide: new THREE.MeshStandardMaterial({ map: crust, roughness: 0.86 }),
      spongeCrumb: new THREE.MeshStandardMaterial({ map: crumb, roughness: 0.92, side: THREE.DoubleSide }),
      jam: new THREE.MeshPhysicalMaterial({ color: '#8c0d2c', roughness: 0.16, clearcoat: 1, clearcoatRoughness: 0.06, sheen: 0.4, sheenColor: '#ff6b8a', side: THREE.DoubleSide }),
      buttercream: new THREE.MeshPhysicalMaterial({ color: '#f6e6c4', roughness: 0.58, sheen: 0.6, sheenColor: '#fff6df', side: THREE.DoubleSide }),
      fondant: new THREE.MeshPhysicalMaterial({ color: '#fbf7f0', roughness: 0.44, sheen: 0.7, sheenColor: '#ffffff', sheenRoughness: 0.5, clearcoat: 0.12, side: THREE.DoubleSide }),
      drum: new THREE.MeshPhysicalMaterial({ color: '#e2e6ee', metalness: 1, roughness: 0.24 }),
      ribbon: new THREE.MeshPhysicalMaterial({ color: '#f1e6cf', roughness: 0.34, sheen: 1, sheenColor: '#fffaf0', sheenRoughness: 0.28, side: THREE.DoubleSide }),
      rose: new THREE.MeshPhysicalMaterial({ color: '#f39bb6', roughness: 0.6, sheen: 1, sheenColor: '#ffd6e2', sheenRoughness: 0.45, side: THREE.DoubleSide }),
      gold: new THREE.MeshPhysicalMaterial({ color: '#f7d06a', metalness: 0.6, roughness: 0.28, clearcoat: 0.8, emissive: '#a86f00', emissiveIntensity: 0.55 }),
      goldBack: new THREE.MeshStandardMaterial({ color: '#b07d27', metalness: 0.6, roughness: 0.35, emissive: '#5a3a00', emissiveIntensity: 0.5 }),
      wick: new THREE.MeshStandardMaterial({ color: '#2a211b', roughness: 1 }),
      flame: new THREE.MeshBasicMaterial({ color: new THREE.Color('#ffb347').multiplyScalar(5), toneMapped: false }),
      flameCore: new THREE.MeshBasicMaterial({ color: new THREE.Color('#fff4d6').multiplyScalar(6), toneMapped: false }),
      card: new THREE.MeshStandardMaterial({ map: paperTexture('#f7f4ef'), roughness: 0.9, transparent: true, opacity: 0.96, side: THREE.DoubleSide }),
      film: new THREE.MeshPhysicalMaterial({ color: '#ffffff', roughness: 0.05, transmission: 0, transparent: true, opacity: 0.16, side: THREE.DoubleSide, depthWrite: false }),
    }
    return m
  }, [])
}

type Mats = ReturnType<typeof useCakeMaterials>

function partMaterials(m: Mats): Partial<Record<PartKey, THREE.MeshStandardMaterial[]>> {
  return {
    sponge: [m.spongeSide, m.spongeCrumb],
    jam: [m.jam],
    buttercream: [m.buttercream],
    fondant: [m.fondant],
    roses: [m.rose],
    ribbon: [m.ribbon],
    drum: [m.drum],
    topper: [m.gold],
    box: [m.card],
  }
}

// ---------------------------------------------------------------- the cake

export function CakeModel({ spec, view, highlight, showBox, lit, anchors, onPart, onHoverPart }: CakeModelProps) {
  const m = useCakeMaterials()
  const { items, top } = useMemo(() => buildStack(spec), [spec])
  const cut = view === 'cutaway'
  const explodeRef = useRef(0)
  const stackRefs = useRef<(THREE.Group | null)[]>([])
  const shellRef = useRef<THREE.Group>(null)
  const boxRef = useRef<THREE.Group>(null)
  const sliceRef = useRef<THREE.Group>(null)
  const byPart = useMemo(() => partMaterials(m), [m])
  const shellLift = items.length * EXPLODE_GAP + 1.5
  const pearlsBase = useMemo(() => ringPoints(CAKE_R + FON_T + 0.05, 64, cut), [cut])
  const pearlsTop = useMemo(() => ringPoints(CAKE_R - 0.1, 52, cut), [cut])

  useFrame((frame, dt) => {
    const target = view === 'exploded' ? 1 : 0
    explodeRef.current = REDUCED_MOTION ? target : THREE.MathUtils.damp(explodeRef.current, target, 3.2, dt)
    const e = explodeRef.current
    items.forEach((it, i) => {
      const g = stackRefs.current[i]
      if (g) g.position.y = it.index * EXPLODE_GAP * e
    })
    if (shellRef.current) shellRef.current.position.y = shellLift * e
    if (boxRef.current) {
      boxRef.current.position.y = THREE.MathUtils.damp(boxRef.current.position.y, showBox ? 0 : 16, 2.6, dt)
      boxRef.current.visible = showBox || boxRef.current.position.y < 15.5
    }
    if (sliceRef.current) {
      const t = frame.clock.elapsedTime
      sliceRef.current.position.set(0, 1.5 + (REDUCED_MOTION ? 0 : Math.sin(t * 1.2) * 0.1), 6.4)
      sliceRef.current.rotation.y = 0.35 + (REDUCED_MOTION ? 0 : Math.sin(t * 0.6) * 0.06)
    }
    // highlighted parts breathe
    const pulse = 0.22 + 0.16 * Math.sin(frame.clock.elapsedTime * 4)
    for (const [key, mats] of Object.entries(byPart) as [PartKey, THREE.MeshStandardMaterial[]][]) {
      const on = highlight.has(key)
      for (const mat of mats) {
        mat.emissive.set(on ? PART_COLOR[key] : '#000000')
        mat.emissiveIntensity = on ? pulse : 0
      }
    }
    // where the material flows land, following the explosion
    const a = anchors.current
    const first = (kind: StackItem['kind']) => items.find((it) => it.kind === kind)
    const at = (it: StackItem | undefined, fallback: number) => (it ? it.y + it.h / 2 + it.index * EXPLODE_GAP * e : fallback)
    const sponges = items.filter((it) => it.kind === 'sponge')
    const mid = sponges[Math.floor(sponges.length / 2)]
    a.sponge = (a.sponge ?? new THREE.Vector3()).set(0, at(mid, 1), 0)
    a.jam = (a.jam ?? new THREE.Vector3()).set(0, at(first('jam'), 1.2), 0)
    a.buttercream = (a.buttercream ?? new THREE.Vector3()).set(0, at(first('buttercream'), 1.3), 0)
    const shellY = shellLift * e
    a.fondant = (a.fondant ?? new THREE.Vector3()).set(0, top + FON_T + shellY, 0)
    a.roses = (a.roses ?? new THREE.Vector3()).set(0, top + FON_T + 0.45 + shellY, 0)
    a.candles = (a.candles ?? new THREE.Vector3()).set(0, top + FON_T + 0.9 + shellY, -CAKE_R * 0.84)
    a.topper = (a.topper ?? new THREE.Vector3()).set(0, top + FON_T + 1.9 + shellY, -1.7)
    a.ribbon = (a.ribbon ?? new THREE.Vector3()).set(0, DRUM_H / 2, DRUM_R)
    a.drum = (a.drum ?? new THREE.Vector3()).set(0, DRUM_H, DRUM_R - 0.5)
    a.piping = (a.piping ?? new THREE.Vector3()).set(0, DRUM_H + 0.15, CAKE_R + FON_T)
    a.box = (a.box ?? new THREE.Vector3()).set(0, (boxRef.current?.position.y ?? 16) + 7.2, 0)
  })

  const handlers = (part: PartKey) => ({
    onClick: (e: ThreeEvent<MouseEvent>) => {
      e.stopPropagation()
      onPart(part)
    },
    onPointerOver: (e: ThreeEvent<PointerEvent>) => {
      e.stopPropagation()
      onHoverPart(part)
    },
    onPointerOut: () => onHoverPart(null),
  })

  const theta = cut ? { start: CUT / 2, length: Math.PI * 2 - CUT } : { start: 0, length: Math.PI * 2 }

  return (
    <group>
      {spec.has.drum && (
        <group {...handlers('drum')}>
          <mesh material={m.drum} position-y={DRUM_H / 2} receiveShadow castShadow>
            <cylinderGeometry args={[DRUM_R, DRUM_R, DRUM_H, 128]} />
          </mesh>
        </group>
      )}
      {spec.has.ribbon && (
        <group {...handlers('ribbon')}>
          <mesh material={m.ribbon} position-y={DRUM_H / 2}>
            <cylinderGeometry args={[DRUM_R + 0.012, DRUM_R + 0.012, DRUM_H * 0.86, 128, 1, true]} />
          </mesh>
        </group>
      )}

      {/* the layer stack */}
      {items.map((it, i) => (
        <group key={i} ref={(g) => void (stackRefs.current[i] = g)} {...handlers(it.kind)}>
          <Slab y={it.y} h={it.h} theta={theta} cut={cut} it={it} m={m} />
        </group>
      ))}

      {/* fondant shell with everything that sits on it */}
      <group ref={shellRef}>
        {spec.has.fondant && (
          <group {...handlers('fondant')}>
            <FondantShell top={top} theta={theta} cut={cut} material={m.fondant} />
          </group>
        )}
        {spec.has.buttercream && (
          <group {...handlers('piping')}>
            <Pearls points={pearlsTop} y={top + FON_T + 0.04} scale={0.095} material={m.buttercream} />
          </group>
        )}
        {spec.roses > 0 && (
          <group {...handlers('roses')}>
            {rosePositions(spec.roses, cut).map(([x, z, r], i) => (
              <group key={i} position={[x, top + FON_T, z]} rotation-y={r}>
                <Rose material={m.rose} />
              </group>
            ))}
          </group>
        )}
        {spec.candles > 0 && (
          <group {...handlers('candles')}>
            {ringPoints(CAKE_R * 0.84, spec.candles, cut, Math.PI / spec.candles).map(([x, z], i) => (
              <Candle key={i} position={[x, top + FON_T, z]} index={i} lit={lit} wick={m.wick} flame={m.flame} core={m.flameCore} />
            ))}
          </group>
        )}
        {spec.has.topper && spec.topperText && (
          <group {...handlers('topper')} position={[0, top + FON_T, -1.75]}>
            <Topper text={spec.topperText} gold={m.gold} back={m.goldBack} />
          </group>
        )}
      </group>

      {spec.has.buttercream && (
        <group {...handlers('piping')}>
          <Pearls points={pearlsBase} y={DRUM_H + 0.12} scale={0.13} material={m.buttercream} />
        </group>
      )}

      {/* a served slice floats out of the cutaway, its apex pulled towards the viewer */}
      {cut && (
        <group ref={sliceRef}>
          <group position-z={-CAKE_R * 0.55}>
            {items.map((it, i) => (
              <Slab key={i} y={it.y} h={it.h} theta={{ start: -CUT / 2, length: CUT }} cut it={it} m={m} />
            ))}
            {spec.has.fondant && <FondantShell top={top} theta={{ start: -CUT / 2, length: CUT }} cut material={m.fondant} />}
          </group>
        </group>
      )}

      {spec.has.box && (
        <group ref={boxRef} position-y={16} visible={false} {...handlers('box')}>
          <CakeBox card={m.card} film={m.film} />
        </group>
      )}
    </group>
  )
}

// ---------------------------------------------------------------- layers

function Slab({ y, h, theta, cut, it, m }: { y: number; h: number; theta: { start: number; length: number }; cut: boolean; it: StackItem; m: Mats }) {
  const geometry = useMemo(() => new THREE.CylinderGeometry(CAKE_R, CAKE_R, h, 128, 1, false, theta.start, theta.length), [h, theta.start, theta.length])
  const materials: THREE.Material[] =
    it.kind === 'sponge' ? [m.spongeSide, m.spongeCrumb, m.spongeCrumb] : it.kind === 'jam' ? [m.jam, m.jam, m.jam] : [m.buttercream, m.buttercream, m.buttercream]
  const face = it.kind === 'sponge' ? m.spongeCrumb : it.kind === 'jam' ? m.jam : m.buttercream
  return (
    <group position-y={y + h / 2}>
      <mesh geometry={geometry} material={materials} castShadow receiveShadow />
      {cut && (
        <>
          <CutFace angle={theta.start} radius={CAKE_R} height={h} material={face} />
          <CutFace angle={theta.start + theta.length} radius={CAKE_R} height={h} material={face} />
        </>
      )}
    </group>
  )
}

function CutFace({ angle, radius, height, material }: { angle: number; radius: number; height: number; material: THREE.Material }) {
  const geometry = useMemo(() => {
    const g = new THREE.PlaneGeometry(radius, height)
    g.translate(radius / 2, 0, 0)
    return g
  }, [radius, height])
  // local +x becomes the radial direction (sin a, 0, cos a) of the cut
  return <mesh geometry={geometry} rotation-y={angle - Math.PI / 2} material={material} />
}

function fondantProfile(top: number): THREE.Vector2[] {
  const outer = CAKE_R + FON_T
  const h = top + FON_T
  const pts: THREE.Vector2[] = [new THREE.Vector2(outer, DRUM_H), new THREE.Vector2(outer, h - EDGE)]
  for (let k = 1; k <= 12; k++) {
    const a = (k / 12) * (Math.PI / 2)
    pts.push(new THREE.Vector2(outer - EDGE + EDGE * Math.cos(a), h - EDGE + EDGE * Math.sin(a)))
  }
  pts.push(new THREE.Vector2(0.0001, h))
  return pts
}

function FondantShell({ top, theta, cut, material }: { top: number; theta: { start: number; length: number }; cut: boolean; material: THREE.Material }) {
  const lathe = useMemo(() => new THREE.LatheGeometry(fondantProfile(top), 160, theta.start, theta.length), [top, theta.start, theta.length])
  // cross-section of the shell on the cut planes: the thin wall and the top sheet
  const section = useMemo(() => {
    const h = top + FON_T
    const outer = CAKE_R + FON_T
    const s = new THREE.Shape()
    s.moveTo(CAKE_R, DRUM_H)
    s.lineTo(outer, DRUM_H)
    s.lineTo(outer, h - EDGE)
    s.absarc(outer - EDGE, h - EDGE, EDGE, 0, Math.PI / 2, false)
    s.lineTo(0, h)
    s.lineTo(0, top)
    s.lineTo(CAKE_R, top)
    s.lineTo(CAKE_R, DRUM_H)
    return new THREE.ShapeGeometry(s, 16)
  }, [top])
  return (
    <group>
      <mesh geometry={lathe} material={material} castShadow receiveShadow />
      {cut &&
        [theta.start, theta.start + theta.length].map((a, i) => (
          <mesh key={i} geometry={section} rotation-y={a - Math.PI / 2} material={material} />
        ))}
    </group>
  )
}

// ---------------------------------------------------------------- decoration

const PETAL = new THREE.SphereGeometry(1, 22, 14, Math.PI / 2 - 0.85, 1.7, 0.18 * Math.PI, 0.46 * Math.PI)
const BUD = new THREE.SphereGeometry(1, 20, 16)

/** A hand-made sugar paste rose: petals spiral out by the golden angle, opening as they grow. */
function Rose({ material }: { material: THREE.Material }) {
  const petals = useMemo(() => {
    const out: { angle: number; radius: number; tilt: number }[] = []
    const rings = [
      { n: 3, radius: 0.16, tilt: 0.05 },
      { n: 5, radius: 0.26, tilt: 0.32 },
      { n: 6, radius: 0.36, tilt: 0.72 },
    ]
    let k = 0
    for (const ring of rings) {
      for (let i = 0; i < ring.n; i++) out.push({ angle: k++ * 2.39996, radius: ring.radius, tilt: ring.tilt })
    }
    return out
  }, [])
  return (
    <group scale={1.65}>
      <mesh geometry={BUD} material={material} position-y={0.1} scale={[0.1, 0.16, 0.1]} castShadow />
      {petals.map((p, i) => (
        <group key={i} rotation-y={p.angle}>
          <mesh geometry={PETAL} material={material} rotation-x={p.tilt} position-y={0.06} scale={[p.radius, p.radius * 1.15, p.radius]} castShadow />
        </group>
      ))}
    </group>
  )
}

const PASTELS: [string, string][] = [
  ['#fbcfe8', '#ffffff'],
  ['#bfdbfe', '#ffffff'],
  ['#fde68a', '#ffffff'],
  ['#bbf7d0', '#ffffff'],
  ['#ddd6fe', '#ffffff'],
]

function Candle({ position, index, lit, wick, flame, core }: { position: [number, number, number]; index: number; lit: boolean; wick: THREE.Material; flame: THREE.Material; core: THREE.Material }) {
  const [a, b] = PASTELS[index % PASTELS.length]
  const body = useMemo(() => new THREE.MeshStandardMaterial({ map: candleTexture(a, b), roughness: 0.55 }), [a, b])
  const flameRef = useRef<THREE.Group>(null)
  const seed = index * 1.37
  useFrame((frame) => {
    const f = flameRef.current
    if (!f) return
    const t = frame.clock.elapsedTime * 9 + seed
    const flick = REDUCED_MOTION ? 1 : 1 + Math.sin(t) * 0.08 + Math.sin(t * 2.3) * 0.05
    f.scale.set(1, flick, 1)
    f.position.x = REDUCED_MOTION ? 0 : Math.sin(t * 0.7) * 0.008
    f.visible = lit
  })
  return (
    <group position={position}>
      <mesh material={body} position-y={0.62} castShadow>
        <cylinderGeometry args={[0.052, 0.058, 1.24, 18]} />
      </mesh>
      <mesh material={wick} position-y={1.29}>
        <cylinderGeometry args={[0.008, 0.008, 0.1, 6]} />
      </mesh>
      <group ref={flameRef} position-y={1.42}>
        <mesh material={flame} scale={[0.055, 0.13, 0.055]}>
          <sphereGeometry args={[1, 16, 12]} />
        </mesh>
        <mesh material={core} scale={[0.024, 0.06, 0.024]} position-y={-0.03}>
          <sphereGeometry args={[1, 12, 8]} />
        </mesh>
      </group>
    </group>
  )
}

function Topper({ text, gold, back }: { text: string; gold: THREE.Material; back: THREE.Material }) {
  return (
    <group>
      {[-0.95, 0.95].map((x) => (
        <mesh key={x} material={gold} position={[x, 0.75, 0]} castShadow>
          <cylinderGeometry args={[0.028, 0.028, 1.5, 10]} />
        </mesh>
      ))}
      <Suspense fallback={null}>
        <group position-y={1.42}>
          <Text font={pacifico} fontSize={0.62} anchorX="center" anchorY="bottom" maxWidth={4.2} textAlign="center" material={back} position-z={-0.035}>
            {text}
          </Text>
          <Text font={pacifico} fontSize={0.62} anchorX="center" anchorY="bottom" maxWidth={4.2} textAlign="center" material={gold} castShadow>
            {text}
          </Text>
        </group>
      </Suspense>
    </group>
  )
}

function Pearls({ points, y, scale, material }: { points: [number, number][]; y: number; scale: number; material: THREE.Material }) {
  const ref = useRef<THREE.InstancedMesh>(null)
  useLayoutEffect(() => {
    const mesh = ref.current
    if (!mesh) return
    const o = new THREE.Object3D()
    points.forEach(([x, z], i) => {
      o.position.set(x, y, z)
      o.scale.set(scale, scale * 0.82, scale)
      o.updateMatrix()
      mesh.setMatrixAt(i, o.matrix)
    })
    mesh.count = points.length
    mesh.instanceMatrix.needsUpdate = true
  }, [points, y, scale])
  return (
    <instancedMesh ref={ref} args={[undefined, material, 96]} castShadow>
      <sphereGeometry args={[1, 14, 10]} />
    </instancedMesh>
  )
}

function CakeBox({ card, film }: { card: THREE.Material; film: THREE.Material }) {
  const w = 10.4
  const h = 7.2
  return (
    <group position-y={h / 2}>
      {/* card walls left, right and back; window film front and lid */}
      <Panel position={[-w / 2, 0, 0]} rotation={[0, Math.PI / 2, 0]} size={[w, h]} material={card} />
      <Panel position={[w / 2, 0, 0]} rotation={[0, Math.PI / 2, 0]} size={[w, h]} material={card} />
      <Panel position={[0, 0, -w / 2]} rotation={[0, 0, 0]} size={[w, h]} material={card} />
      <Panel position={[0, 0, w / 2]} rotation={[0, 0, 0]} size={[w, h]} material={film} />
      <Panel position={[0, h / 2, 0]} rotation={[-Math.PI / 2, 0, 0]} size={[w, w]} material={film} />
      <mesh>
        <boxGeometry args={[w, h, w]} />
        <meshBasicMaterial visible={false} />
        <Edges color="#e9d5ff" threshold={15} />
      </mesh>
    </group>
  )
}

function Panel({ position, rotation, size, material }: { position: [number, number, number]; rotation: [number, number, number]; size: [number, number]; material: THREE.Material }) {
  return (
    <mesh position={position} rotation={rotation} material={material}>
      <planeGeometry args={size} />
    </mesh>
  )
}

// ---------------------------------------------------------------- placement helpers

function inCut(angle: number): boolean {
  const a = Math.atan2(Math.sin(angle), Math.cos(angle))
  return Math.abs(a) < CUT / 2 + 0.08
}

/** Points on a ring (x, z), angle measured from +z like the cylinders; the cut wedge is left empty. */
function ringPoints(radius: number, count: number, cut: boolean, offset = 0): [number, number][] {
  const out: [number, number][] = []
  for (let i = 0; i < count; i++) {
    const a = offset + (i / count) * Math.PI * 2
    if (cut && inCut(a)) continue
    out.push([Math.sin(a) * radius, Math.cos(a) * radius])
  }
  return out
}

function rosePositions(count: number, cut: boolean): [number, number, number][] {
  if (count === 0) return []
  const out: [number, number, number][] = [[0, 0.35, 0]]
  const around = count - 1
  for (let i = 0; i < around; i++) {
    const a = Math.PI / 4 + (i / Math.max(1, around)) * Math.PI * 2
    if (cut && inCut(a)) continue
    out.push([Math.sin(a) * 1.35, Math.cos(a) * 1.35 + 0.35, a])
  }
  return out
}

export function Lazy({ children }: { children: ReactNode }) {
  return <Suspense fallback={null}>{children}</Suspense>
}
