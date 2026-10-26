import { useId } from 'react'
import type { ArtParams } from '../catalog'
import { sprocketPath } from '../geometry'
import { band } from './defs'
import { Bar, Drum, Face } from './rig'

// The wheels' artwork: rims, hubs, spokes, nipples, rim tape, rotors, cassette, tyres and inner tubes.

const circle = (r: number) => `M${-r},0 a${r},${r} 0 1,1 ${2 * r},0 a${r},${r} 0 1,1 ${-2 * r},0`

function Holes({ n, r, size, fill = '#000' }: { n: number; r: number; size: number; fill?: string }) {
  return (
    <>
      {[...Array(n).keys()].map((k) => {
        const a = (k / n) * Math.PI * 2 - Math.PI / 2
        return <circle key={k} cx={Math.cos(a) * r} cy={Math.sin(a) * r} r={size} fill={fill} />
      })}
    </>
  )
}

export function RimArt({ p }: { p: ArtParams }) {
  const id = useId()
  const o = p.outer ?? 318
  const i = p.inner ?? 290
  const mid = (o + i) / 2
  const w = o - i
  return (
    <>
      <Face>
        <circle r={mid} fill="none" stroke="#060709" strokeWidth={w} />
      </Face>
      <Face z={p.depth ?? 24}>
        <circle r={mid} fill="none" stroke="#16181d" strokeWidth={w} />
        <circle r={mid + w * 0.18} fill="none" stroke="#262a31" strokeWidth={w * 0.34} />
        <circle r={o - 0.8} fill="none" stroke="#6b7380" strokeWidth={1.2} strokeOpacity={0.7} />
        <circle r={i + 0.8} fill="none" stroke="#3b414b" strokeWidth={1} />
        <Holes n={p.holes ?? 24} r={i + 4} size={1.5} />
        <circle cy={i + 4} r={3.4} fill="#000" />
        <path id={id} d={circle(mid - 2.4)} fill="none" />
        <text fontSize={7} letterSpacing={1.6} fill="#8b95a5" fontWeight={600}>
          <textPath href={`#${id}`} startOffset="12%">
            {p.text}
          </textPath>
        </text>
        <text fontSize={7} letterSpacing={1.6} fill="#8b95a5" fontWeight={600}>
          <textPath href={`#${id}`} startOffset="62%">
            {p.text}
          </textPath>
        </text>
      </Face>
    </>
  )
}

export function HubArt({ p }: { p: ArtParams }) {
  const [z0, z1, disc, axle] = p.zs ?? [-35, 35, -42, 50]
  const r0 = p.r ?? 22
  const r1 = p.outer ?? 22
  const rear = p.which === 'rear'
  const holes = p.holes ?? 12
  const flange = (r: number) => (
    <>
      <circle r={r} fill="url(#sheen-dark)" stroke="#59606b" strokeWidth={0.8} />
      <Holes n={holes} r={r - 4.5} size={1.3} fill="#9aa3ad" />
    </>
  )
  return (
    <>
      <Bar a={[0, 0, -axle]} b={[0, 0, axle - 6]} r={6} fill={band('silver')} cap={false} />
      <Face z={disc}>
        <circle r={19} fill="url(#sheen-dark)" />
        <Holes n={12} r={17} size={1.6} fill="#05060a" />
        <circle r={12} fill="#0a0b0e" />
      </Face>
      <Face z={z0}>{flange(r0)}</Face>
      <Bar a={[0, 0, z0]} b={[0, 0, z1]} r={12.5} fill={band('black')} cap={false} />
      <Face z={z1}>{flange(r1)}</Face>
      {rear && <Bar a={[0, 0, z1 + 2]} b={[0, 0, axle - 8]} r={17} fill={band('steel')} cap={<circle r={1} fill="url(#cap-shine)" />} />}
      <Bar a={[0, 0, axle - 6]} b={[0, 0, axle]} r={7.5} fill={band('silver')} cap={<><circle r={1} fill="url(#cap-shine)" /><circle r={0.45} fill="#111317" /></>} />
    </>
  )
}

export function SpokeArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 290
  return (
    <Face>
      <rect x={0} y={-0.95} width={L} height={1.9} rx={0.9} fill={band('silver')} />
      <rect x={L - 11} y={-1} width={11} height={2} fill="#8d96a3" />
      <path d="M0.5,-0.9 q-3.4,0 -3.4,3.6" fill="none" stroke="#c7ced8" strokeWidth={1.8} strokeLinecap="round" />
    </Face>
  )
}

export function NippleArt() {
  return (
    <Face>
      <rect x={-12} y={-1.8} width={12} height={3.6} rx={0.8} fill={band('brass')} />
      <rect x={-12.5} y={-2.4} width={3} height={4.8} rx={0.6} fill={band('brass')} />
    </Face>
  )
}

export function TapeRollArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 44
  const i = p.inner ?? 19
  return (
    <Drum r={r} h={25} side="#ca9a0a">
      <circle r={r} fill="#facc15" />
      {[0.94, 0.86, 0.78, 0.7, 0.62, 0.54].map((k) => (
        <circle key={k} r={r * k} fill="none" stroke="#eab308" strokeWidth={0.7} />
      ))}
      <circle r={i + 4} fill="#b88d55" />
      <circle r={i} fill="#0b0c10" />
      <ellipse cx={-r * 0.35} cy={-r * 0.45} rx={r * 0.32} ry={r * 0.12} fill="#ffffff" opacity={0.25} />
    </Drum>
  )
}

export function TapeRingArt({ p }: { p: ArtParams }) {
  return (
    <Face>
      <circle r={p.r ?? 312} fill="none" stroke="#facc15" strokeWidth={3.4} pathLength={1} strokeDasharray="1 1" style={{ strokeDashoffset: 'calc(1 - var(--m, 1))' }} />
    </Face>
  )
}

export function RotorArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 80
  const track = r * 0.74
  const carrier = r * 0.34
  const arms = [...Array(6).keys()].map((k) => {
    const a = (k / 6) * Math.PI * 2
    const b = a + 0.38
    const p0 = [Math.cos(a) * carrier, Math.sin(a) * carrier]
    const p1 = [Math.cos(b + 0.22) * (track + 2), Math.sin(b + 0.22) * (track + 2)]
    const p2 = [Math.cos(b + 0.5) * (track + 2), Math.sin(b + 0.5) * (track + 2)]
    const p3 = [Math.cos(a + 0.55) * carrier, Math.sin(a + 0.55) * carrier]
    return `M${p0.join(',')} Q${Math.cos(a + 0.2) * r * 0.55},${Math.sin(a + 0.2) * r * 0.55} ${p1.join(',')} L${p2.join(',')} Q${Math.cos(a + 0.62) * r * 0.55},${Math.sin(a + 0.62) * r * 0.55} ${p3.join(',')} Z`
  })
  return (
    <Face>
      <path d={circle(r) + circle(track)} fillRule="evenodd" fill="url(#sheen)" />
      <Holes n={24} r={(r + track) / 2} size={r * 0.028} fill="#0b0c10" />
      {arms.map((d, k) => (
        <path key={k} d={d} fill="url(#sheen)" />
      ))}
      <circle r={carrier + 2} fill="url(#sheen-dark)" />
      <Holes n={6} r={carrier - 4} size={2} fill="#9aa3ad" />
      <circle r={carrier * 0.52} fill="#07080b" />
    </Face>
  )
}

export function CassetteArt({ p }: { p: ArtParams }) {
  const teeth = p.teeth ?? [11, 13, 15, 17, 19, 21, 24, 28, 34]
  const zs = p.zs ?? teeth.map((_, i) => 61 - i * 3.7)
  const order = teeth.map((_, i) => i).reverse()
  return (
    <>
      {order.map((i) => (
        <Face key={i} z={zs[i]}>
          <path d={sprocketPath(teeth[i], 17)} fillRule="evenodd" fill={i >= teeth.length - 3 ? 'url(#sheen-dark)' : 'url(#sheen)'} stroke="#3b414b" strokeWidth={0.5} />
        </Face>
      ))}
      <Face z={zs[0] + 1}>
        <circle r={16} fill="url(#sheen-dark)" />
        <circle r={7} fill="#05060a" />
      </Face>
    </>
  )
}

const foldedFade = { opacity: 'calc(1 - var(--m, 0) * 1.6)' }
const roundFade = { opacity: 'calc(var(--m, 1) * 2.4 - 1.4)' }

export function TyreArt({ p }: { p: ArtParams }) {
  const id = useId()
  const o = p.outer ?? 340
  const i = p.inner ?? 308
  const w = o - i
  const mid = (o + i) / 2
  return (
    <>
      <g style={foldedFade}>
        <Face>
          {[-14, 14].map((dy, k) => (
            <g key={k}>
              <ellipse cy={dy} rx={118} ry={44} fill="none" stroke="#0e0f11" strokeWidth={26} />
              <ellipse cy={dy} rx={118} ry={44} fill="none" stroke="#2a2b30" strokeWidth={12} strokeDasharray="3.5 3" />
            </g>
          ))}
          <rect x={-44} y={-13} width={88} height={26} rx={5} fill="#facc15" />
          <text y={5} textAnchor="middle" fontSize={12} fontWeight={800} fill="#1c1917">
            {(p.text ?? '').split(' · ')[0]}
          </text>
        </Face>
      </g>
      <g style={roundFade}>
        <Face>
          <circle r={mid} fill="none" stroke="#040405" strokeWidth={w} />
        </Face>
        <Face z={p.depth ?? 32}>
          <circle r={mid} fill="none" stroke="#111215" strokeWidth={w} />
          <circle r={o - w * 0.2} fill="none" stroke="#2b2c31" strokeWidth={w * 0.36} strokeDasharray="4.2 3.4" />
          <circle r={i + w * 0.3} fill="none" stroke="#1b1c20" strokeWidth={w * 0.28} />
          <path d={`M${-mid * 0.94},${-mid * 0.34} A${mid},${mid} 0 0 1 ${-mid * 0.34},${-mid * 0.94}`} fill="none" stroke="#ffffff" strokeOpacity={0.07} strokeWidth={w * 0.55} strokeLinecap="round" />
          <path id={id} d={circle(i + w * 0.3)} fill="none" />
          <text fontSize={7.4} letterSpacing={1.4} fill="#7b8290" fontWeight={700}>
            <textPath href={`#${id}`} startOffset="8%">
              {p.text}
            </textPath>
          </text>
          <text fontSize={7.4} letterSpacing={1.4} fill="#7b8290" fontWeight={700}>
            <textPath href={`#${id}`} startOffset="58%">
              {p.text}
            </textPath>
          </text>
        </Face>
      </g>
    </>
  )
}

export function InnerTubeArt({ p }: { p: ArtParams }) {
  const o = p.outer ?? 325
  const i = p.inner ?? 303
  const mid = (o + i) / 2
  return (
    <>
      <g style={foldedFade}>
        <Face>
          <ellipse rx={74} ry={30} fill="none" stroke="#1d1e22" strokeWidth={13} />
          <ellipse rx={56} ry={20} cy={4} fill="none" stroke="#2a2b30" strokeWidth={11} />
          <rect x={58} y={-4} width={26} height={8} rx={2} fill={band('silver')} />
          <rect x={80} y={-5} width={9} height={10} rx={2} fill="#dc2626" />
        </Face>
      </g>
      <g style={roundFade}>
        <Face>
          <circle r={mid} fill="none" stroke="#24252a" strokeWidth={o - i} />
          <rect x={-2.6} y={i - 34} width={5.2} height={36} rx={2} fill={band('silver')} />
          <rect x={-3.4} y={i - 44} width={6.8} height={11} rx={2} fill="#dc2626" />
        </Face>
      </g>
    </>
  )
}
