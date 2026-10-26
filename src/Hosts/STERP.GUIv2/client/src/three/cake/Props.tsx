import { RoundedBox } from '@react-three/drei'
import { useMemo } from 'react'
import * as THREE from 'three'
import type { PropKind } from '@/components/bom/studioModel'
import { candleTexture, labelTexture, paperTexture } from './textures'

// Small procedural props for the purchased materials around the cake. About 1.2 units tall, standing on y = 0.

const shared = (() => {
  let m: ReturnType<typeof make> | null = null
  function make() {
    return {
      kraft: new THREE.MeshStandardMaterial({ map: paperTexture('#d8c09a'), roughness: 0.95 }),
      glass: new THREE.MeshPhysicalMaterial({ color: '#ffffff', transmission: 1, roughness: 0.05, thickness: 0.2, ior: 1.45 }),
      ceramic: new THREE.MeshPhysicalMaterial({ color: '#fbfaf7', roughness: 0.16, clearcoat: 1, clearcoatRoughness: 0.05 }),
      foil: new THREE.MeshPhysicalMaterial({ color: '#d9b04c', metalness: 1, roughness: 0.32 }),
      butter: new THREE.MeshStandardMaterial({ color: '#f8df84', roughness: 0.5 }),
      powder: new THREE.MeshStandardMaterial({ color: '#ffffff', roughness: 1 }),
      wood: new THREE.MeshStandardMaterial({ color: '#b5814f', roughness: 0.72 }),
      silver: new THREE.MeshPhysicalMaterial({ color: '#e5e7eb', metalness: 1, roughness: 0.24 }),
      dark: new THREE.MeshStandardMaterial({ color: '#25232a', roughness: 0.45 }),
      card: new THREE.MeshStandardMaterial({ map: paperTexture('#f6f3ee'), roughness: 0.9 }),
      eggshell: new THREE.MeshPhysicalMaterial({ color: '#f1dcc0', roughness: 0.6, sheen: 0.3 }),
      parchment: new THREE.MeshStandardMaterial({ color: '#efe3c8', roughness: 0.85, side: THREE.DoubleSide }),
      film: new THREE.MeshPhysicalMaterial({ color: '#f8fbff', roughness: 0.15, transparent: true, opacity: 0.45, side: THREE.DoubleSide }),
      sand: new THREE.MeshStandardMaterial({ color: '#f1c27d', roughness: 1 }),
    }
  }
  return () => (m ??= make())
})()

function tinted(color: string, options: THREE.MeshPhysicalMaterialParameters = {}) {
  return new THREE.MeshPhysicalMaterial({ color, roughness: 0.4, ...options })
}

function lathe(points: [number, number][], segments = 48) {
  return new THREE.LatheGeometry(
    points.map(([r, y]) => new THREE.Vector2(r, y)),
    segments,
  )
}

export function Prop({ kind, tint, title }: { kind: PropKind; tint: string; title: string }) {
  const m = shared()
  switch (kind) {
    case 'sack':
      return <Sack title={title} tint={tint} />
    case 'jar':
      return <Jar fill={tint} lid={tint === '#9f1239' ? '#dc2626' : '#f5f5f4'} title={title} />
    case 'bowl':
      return <Bowl content={tint} mound />
    case 'crumbs':
      return <Bowl content={tint} crumbs />
    case 'butter':
      return (
        <group>
          <RoundedBox args={[1.05, 0.42, 0.62]} radius={0.06} position={[0, 0.21, 0]} material={m.foil} castShadow />
          <RoundedBox args={[0.62, 0.36, 0.56]} radius={0.05} position={[0.36, 0.48, 0]} material={m.butter} castShadow />
        </group>
      )
    case 'eggs':
      return <Eggs />
    case 'bottle':
      return <Bottle fill={tint} cap="#2563eb" tall />
    case 'vial':
      return <Bottle fill={tint} cap="#18181b" />
    case 'tin':
      return <Tin color={tint} title={title} />
    case 'sachet':
      return (
        <group rotation={[-0.28, 0, 0]}>
          <RoundedBox args={[0.82, 1.05, 0.14]} radius={0.05} position={[0, 0.55, 0]} castShadow>
            <meshStandardMaterial map={labelTexture(title, '#d97706')} roughness={0.6} />
          </RoundedBox>
        </group>
      )
    case 'block':
      return (
        <group>
          <RoundedBox args={[0.9, 0.5, 0.7]} radius={0.12} position={[0, 0.25, 0]} castShadow>
            <meshPhysicalMaterial color={tint} roughness={0.7} sheen={0.6} />
          </RoundedBox>
          <RoundedBox args={[0.96, 0.54, 0.76]} radius={0.13} position={[0, 0.26, 0]} material={m.film} />
        </group>
      )
    case 'tube':
      return <Tube color={tint} />
    case 'wire':
      return <Wire color={tint} />
    case 'discs':
      return (
        <group>
          {[...Array(7).keys()].map((i) => (
            <mesh key={i} material={m.parchment} position={[Math.sin(i * 1.7) * 0.04, 0.03 + i * 0.045, Math.cos(i * 2.1) * 0.04]} castShadow>
              <cylinderGeometry args={[0.62, 0.62, 0.012, 48]} />
            </mesh>
          ))}
        </group>
      )
    case 'cones':
      return (
        <group>
          {[-0.28, 0.3].map((x, i) => (
            <mesh key={x} material={m.film} position={[x, 0.6, i * 0.2]} rotation={[0, 0, i ? -0.5 : 0.45]} castShadow>
              <coneGeometry args={[0.28, 1.1, 32, 1, true]} />
            </mesh>
          ))}
        </group>
      )
    case 'spool':
      return (
        <group rotation={[Math.PI / 2, 0, 0]} position-y={0.48}>
          <mesh material={m.wood} castShadow>
            <cylinderGeometry args={[0.48, 0.48, 0.06, 40]} />
          </mesh>
          <mesh material={m.wood} position-y={0.52}>
            <cylinderGeometry args={[0.48, 0.48, 0.06, 40]} />
          </mesh>
          <mesh position-y={0.26} castShadow>
            <cylinderGeometry args={[0.4, 0.4, 0.46, 40]} />
            <meshPhysicalMaterial color={tint} roughness={0.32} sheen={1} sheenColor="#ffffff" />
          </mesh>
        </group>
      )
    case 'candlebox':
      return <CandleBox />
    case 'board':
      return (
        <mesh material={m.silver} position-y={0.05} castShadow>
          <cylinderGeometry args={[0.78, 0.78, 0.08, 64]} />
        </mesh>
      )
    case 'box':
      return (
        <group>
          <RoundedBox args={[1.1, 0.72, 1.1]} radius={0.02} position={[0, 0.36, 0]} material={m.card} castShadow />
          <mesh material={m.film} position={[0, 0.4, 0.552]}>
            <planeGeometry args={[0.7, 0.42]} />
          </mesh>
        </group>
      )
    case 'hourglass':
      return <Hourglass />
    default:
      return (
        <group>
          <RoundedBox args={[0.95, 0.75, 0.95]} radius={0.04} position={[0, 0.375, 0]} material={m.kraft} castShadow />
          <mesh position={[0, 0.752, 0]} rotation-x={-Math.PI / 2}>
            <planeGeometry args={[0.95, 0.18]} />
            <meshStandardMaterial color={tint} roughness={0.5} />
          </mesh>
        </group>
      )
  }
}

function Sack({ title, tint }: { title: string; tint: string }) {
  const m = shared()
  const label = useMemo(() => labelTexture(title, '#7c5c3b'), [title])
  return (
    <group>
      <RoundedBox args={[0.92, 1.18, 0.6]} radius={0.12} smoothness={5} position={[0, 0.59, 0]} material={m.kraft} castShadow />
      <RoundedBox args={[0.88, 0.22, 0.42]} radius={0.06} position={[0, 1.24, 0]} rotation={[0.25, 0, 0]} material={m.kraft} castShadow />
      <mesh position={[0, 0.58, 0.303]}>
        <planeGeometry args={[0.74, 0.62]} />
        <meshStandardMaterial map={label} roughness={0.8} />
      </mesh>
      <mesh position={[0, 1.36, 0]}>
        <sphereGeometry args={[0.06, 12, 8]} />
        <meshStandardMaterial color={tint} />
      </mesh>
    </group>
  )
}

function Jar({ fill, lid, title }: { fill: string; lid: string; title: string }) {
  const m = shared()
  const body = useMemo(() => lathe([[0.001, 0], [0.42, 0], [0.48, 0.08], [0.5, 0.2], [0.5, 0.92], [0.42, 1.02], [0.38, 1.08], [0.38, 1.12]]), [])
  const filling = useMemo(() => tinted(fill, { roughness: fill === '#9f1239' ? 0.2 : 0.75, clearcoat: fill === '#9f1239' ? 1 : 0 }), [fill])
  const label = useMemo(() => labelTexture(title, '#be123c'), [title])
  return (
    <group>
      <mesh geometry={body} material={m.glass} castShadow />
      <mesh material={filling} position-y={0.43}>
        <cylinderGeometry args={[0.44, 0.44, 0.78, 40]} />
      </mesh>
      <mesh position-y={1.17} castShadow>
        <cylinderGeometry args={[0.42, 0.42, 0.14, 40]} />
        <meshPhysicalMaterial color={lid} roughness={0.35} clearcoat={0.8} />
      </mesh>
      <mesh position={[0, 0.56, 0]}>
        <cylinderGeometry args={[0.505, 0.505, 0.36, 40, 1, true, -0.7, 1.4]} />
        <meshStandardMaterial map={label} roughness={0.7} side={THREE.DoubleSide} />
      </mesh>
    </group>
  )
}

function Bowl({ content, mound, crumbs }: { content: string; mound?: boolean; crumbs?: boolean }) {
  const m = shared()
  const bowl = useMemo(() => lathe([[0.001, 0], [0.32, 0], [0.36, 0.04], [0.62, 0.36], [0.66, 0.46], [0.6, 0.46], [0.56, 0.38], [0.3, 0.08], [0.001, 0.08]], 64), [])
  const crumbPoints = useMemo(
    () => [...Array(36).keys()].map((i) => [Math.cos(i * 2.4) * (0.08 + (i % 7) * 0.06), 0.42 + (i % 5) * 0.035, Math.sin(i * 2.4) * (0.08 + (i % 7) * 0.06)] as [number, number, number]),
    [],
  )
  const fill = useMemo(() => tinted(content, { roughness: 0.95 }), [content])
  return (
    <group>
      <mesh geometry={bowl} material={m.ceramic} castShadow />
      {mound && (
        <mesh material={m.powder} position-y={0.36} scale={[0.52, 0.26, 0.52]}>
          <sphereGeometry args={[1, 32, 16, 0, Math.PI * 2, 0, Math.PI / 2]} />
        </mesh>
      )}
      {crumbs &&
        crumbPoints.map((p, i) => (
          <mesh key={i} material={fill} position={p} scale={0.07 + (i % 3) * 0.02}>
            <dodecahedronGeometry args={[1, 0]} />
          </mesh>
        ))}
    </group>
  )
}

function Eggs() {
  const m = shared()
  const egg = useMemo(() => {
    const pts: [number, number][] = []
    for (let i = 0; i <= 20; i++) {
      const t = i / 20
      const y = t * 0.62
      const r = 0.22 * Math.sin(Math.PI * t) * (1 - 0.18 * t) + 0.0001
      pts.push([r, y])
    }
    return lathe(pts, 32)
  }, [])
  return (
    <group>
      <mesh material={m.ceramic} position-y={0.04}>
        <cylinderGeometry args={[0.72, 0.66, 0.08, 48]} />
      </mesh>
      {[
        [-0.26, 0.06, 0.1, 0.25],
        [0.24, 0.06, 0.14, -0.3],
        [0, 0.06, -0.24, 0.05],
      ].map(([x, y, z, r], i) => (
        <mesh key={i} geometry={egg} material={m.eggshell} position={[x, y, z]} rotation={[0, 0, r]} castShadow />
      ))}
    </group>
  )
}

function Bottle({ fill, cap, tall }: { fill: string; cap: string; tall?: boolean }) {
  const m = shared()
  const h = tall ? 1.35 : 0.85
  const w = tall ? 0.34 : 0.26
  const body = useMemo(
    () => lathe([[0.001, 0], [w * 0.9, 0], [w, 0.06], [w, h * 0.62], [w * 0.55, h * 0.82], [w * 0.36, h * 0.9], [w * 0.36, h]], 40),
    [h, w],
  )
  const liquid = useMemo(() => tinted(fill, { roughness: 0.25, transmission: fill === '#dbeafe' ? 0.6 : 0, transparent: fill === '#dbeafe', opacity: 0.85 }), [fill])
  return (
    <group>
      <mesh geometry={body} material={m.glass} castShadow />
      <mesh material={liquid} position-y={h * 0.32}>
        <cylinderGeometry args={[w * 0.9, w * 0.9, h * 0.6, 32]} />
      </mesh>
      <mesh position-y={h + 0.06} castShadow>
        <cylinderGeometry args={[w * 0.4, w * 0.4, 0.14, 24]} />
        <meshPhysicalMaterial color={cap} roughness={0.35} clearcoat={0.6} />
      </mesh>
    </group>
  )
}

function Tin({ color, title }: { color: string; title: string }) {
  const m = shared()
  const label = useMemo(() => labelTexture(title, color), [title, color])
  return (
    <group>
      <mesh position-y={0.46} castShadow>
        <cylinderGeometry args={[0.38, 0.38, 0.92, 48]} />
        <meshStandardMaterial map={label} roughness={0.45} metalness={0.2} />
      </mesh>
      <mesh material={m.silver} position-y={0.96}>
        <cylinderGeometry args={[0.4, 0.4, 0.08, 48]} />
      </mesh>
    </group>
  )
}

function Tube({ color }: { color: string }) {
  return (
    <group rotation={[0, 0.4, Math.PI / 2.3]} position={[0.1, 0.2, 0]}>
      <mesh castShadow>
        <capsuleGeometry args={[0.16, 0.7, 8, 20]} />
        <meshPhysicalMaterial color={color} roughness={0.3} clearcoat={1} />
      </mesh>
      <mesh position-y={0.56}>
        <cylinderGeometry args={[0.08, 0.1, 0.22, 16]} />
        <meshStandardMaterial color="#f8fafc" />
      </mesh>
    </group>
  )
}

function Wire({ color }: { color: string }) {
  const m = shared()
  return (
    <group rotation={[0, 0, 0.12]}>
      {[...Array(9).keys()].map((i) => (
        <mesh key={i} position={[Math.cos(i * 0.7) * 0.07, 0.75, Math.sin(i * 0.7) * 0.07]} rotation={[Math.sin(i) * 0.08, 0, Math.cos(i) * 0.08]} castShadow>
          <cylinderGeometry args={[0.012, 0.012, 1.5, 6]} />
          <meshStandardMaterial color={color} roughness={0.5} />
        </mesh>
      ))}
      <mesh material={m.wood} position-y={0.75}>
        <torusGeometry args={[0.11, 0.025, 8, 24]} />
      </mesh>
    </group>
  )
}

function CandleBox() {
  const m = shared()
  const colours = ['#fbcfe8', '#bfdbfe', '#fde68a', '#bbf7d0']
  return (
    <group>
      <RoundedBox args={[1.1, 0.22, 0.6]} radius={0.03} position={[0, 0.11, 0]} material={m.card} castShadow />
      {colours.map((c, i) => (
        <mesh key={c} position={[-0.36 + i * 0.24, 0.36, 0]} rotation={[Math.PI / 2, 0, 0.1 * i]} castShadow>
          <cylinderGeometry args={[0.045, 0.045, 0.85, 12]} />
          <meshStandardMaterial map={candleTexture(c, '#ffffff')} roughness={0.5} />
        </mesh>
      ))}
    </group>
  )
}

function Hourglass() {
  const m = shared()
  const glass = useMemo(() => lathe([[0.001, 0.08], [0.32, 0.1], [0.3, 0.32], [0.06, 0.62], [0.3, 0.92], [0.32, 1.14], [0.001, 1.16]], 40), [])
  return (
    <group>
      <mesh material={m.wood} position-y={0.04}>
        <cylinderGeometry args={[0.42, 0.42, 0.08, 32]} />
      </mesh>
      <mesh material={m.wood} position-y={1.2}>
        <cylinderGeometry args={[0.42, 0.42, 0.08, 32]} />
      </mesh>
      {[0, 1, 2].map((i) => (
        <mesh key={i} material={m.wood} position={[Math.sin((i / 3) * Math.PI * 2) * 0.36, 0.62, Math.cos((i / 3) * Math.PI * 2) * 0.36]}>
          <cylinderGeometry args={[0.025, 0.025, 1.12, 8]} />
        </mesh>
      ))}
      <mesh geometry={glass} material={m.glass} />
      <mesh material={m.sand} position-y={0.2} scale={[0.26, 0.12, 0.26]}>
        <sphereGeometry args={[1, 20, 10, 0, Math.PI * 2, 0, Math.PI / 2]} />
      </mesh>
      <mesh material={m.sand} position-y={0.78}>
        <coneGeometry args={[0.2, 0.22, 20]} />
      </mesh>
    </group>
  )
}
