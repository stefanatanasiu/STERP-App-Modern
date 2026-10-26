import type { ArtParams } from '../catalog'
import { band } from './defs'
import { Bar, Drum, Face } from './rig'

// The frame family's artwork. Dimensions are millimetres; flat artwork is drawn in SVG coordinates (y down).

const fade = { opacity: 'calc(1 - var(--m, 0))' }
const coatStyle = (v: string) => ({ opacity: `var(${v}, 0)` })

export function TubeArt({ p, coat }: { p: ArtParams; coat?: boolean }) {
  const L = p.length ?? 300
  const d = p.diameter ?? 30
  const r = d / 2
  const rx = Math.min(r * 0.3, 5)
  const shape = { x: 0, y: -r, width: L, height: d, rx }
  return (
    <Face>
      <rect {...shape} fill={band('alloy')} />
      {coat && (
        <>
          <rect {...shape} fill={band('primer')} style={coatStyle('--primer')} />
          <rect {...shape} fill={band('paint')} style={coatStyle('--paint')} />
          <rect {...shape} fill={band('heat')} style={coatStyle('--heat')} />
        </>
      )}
      <rect x={Math.min(L * 0.1, r)} y={-r * 0.5} width={Math.max(0, L - Math.min(L * 0.2, 2 * r))} height={Math.max(0.6, d * 0.07)} rx={0.5} fill="#ffffff" opacity={0.16} />
      <g style={fade}>
        <ellipse cx={1.2} cy={0} rx={r * 0.16} ry={r * 0.92} fill="#1d2127" opacity={0.75} />
        <ellipse cx={L - 1.2} cy={0} rx={r * 0.16} ry={r * 0.92} fill="#8c939d" opacity={0.7} />
      </g>
    </Face>
  )
}

export function BeadArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 8
  return (
    <Face>
      <ellipse rx={r * 2.8} ry={r * 2.3} fill="url(#glow-warm)" style={fade} />
      <ellipse rx={r * 1.25} ry={r * 0.85} fill={band('steel')} />
      <ellipse rx={r * 1.25} ry={r * 0.85} fill={band('paint')} style={coatStyle('--paint')} />
      <path d={`M${-r * 0.8},${-r * 0.2} q${r * 0.4},${-r * 0.5} ${r * 0.8},0 q${r * 0.4},${-r * 0.5} ${r * 0.8},0`} stroke="#ffffff" strokeOpacity={0.35} strokeWidth={0.8} fill="none" />
      <ellipse rx={r * 1.05} ry={r * 0.66} fill="#ffcf7a" style={fade} />
    </Face>
  )
}

export function SpoolArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 95
  return (
    <Drum r={r} h={55} side={band('black')}>
      <circle r={r} fill="url(#sheen-dark)" />
      <circle r={r * 0.86} fill="#8a929c" />
      {[0.84, 0.78, 0.72, 0.66, 0.6, 0.54].map((k) => (
        <circle key={k} r={r * k} fill="none" stroke={k > 0.7 ? '#e7ebf0' : '#c3c9d1'} strokeWidth={r * 0.028} strokeOpacity={0.85} />
      ))}
      <circle r={r * 0.5} fill="url(#sheen-dark)" />
      <circle r={r * 0.2} fill="#06070a" />
      <rect x={-r * 0.42} y={r * 0.24} width={r * 0.84} height={r * 0.2} rx={3} fill="#f8fafc" />
      <text y={r * 0.38} textAnchor="middle" fontSize={r * 0.12} fontWeight={700} fill="#0f172a">
        {p.text}
      </text>
    </Drum>
  )
}

export function CylinderArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 1380
  const d = p.diameter ?? 220
  const r = d / 2
  const body = L - r * 1.4
  return (
    <Face>
      <path d={`M${r * 0.5},${-r} H${body} C${body + r * 0.95},${-r} ${body + r * 0.95},${r} ${body},${r} H${r * 0.5} Q0,${r} 0,${r * 0.5} V${-r * 0.5} Q0,${-r} ${r * 0.5},${-r} Z`} fill="#1d4a3f" />
      <path d={`M${body - r * 0.7},${-r} H${body} C${body + r * 0.95},${-r} ${body + r * 0.95},${r} ${body},${r} H${body - r * 0.7} Z`} fill="#1f6fa8" />
      <path d={`M${r * 0.5},${-r} H${body} C${body + r * 0.95},${-r} ${body + r * 0.95},${r} ${body},${r} H${r * 0.5} Q0,${r} 0,${r * 0.5} V${-r * 0.5} Q0,${-r} ${r * 0.5},${-r} Z`} fill="url(#band-shade)" />
      <rect x={body + r * 0.62} y={-r * 0.26} width={r * 0.55} height={r * 0.52} rx={6} fill={band('brass')} />
      <rect x={body + r * 1.12} y={-r * 0.16} width={r * 0.28} height={r * 0.32} rx={4} fill={band('steel')} />
      <rect x={L * 0.28} y={-r * 0.42} width={L * 0.3} height={r * 0.84} rx={8} fill="#f8fafc" opacity={0.92} />
      <text x={L * 0.43} y={r * 0.14} textAnchor="middle" fontSize={r * 0.36} fontWeight={800} letterSpacing={6} fill="#14532d">
        {p.text}
      </text>
      <path d={`M${L * 0.65},0 l${r * 0.24},${-r * 0.24} l${r * 0.24},${r * 0.24} l${-r * 0.24},${r * 0.24} Z`} fill="#22c55e" stroke="#f8fafc" strokeWidth={3} />
    </Face>
  )
}

/** Curly aluminium swarf, laid out from a fixed seed so it always looks the same. */
export function SwarfArt() {
  const curls: string[] = []
  let s = 7
  const rnd = () => ((s = (s * 9301 + 49297) % 233280) / 233280)
  for (let i = 0; i < 11; i++) {
    const cx = 18 + rnd() * 154
    const cy = 18 + rnd() * 74
    const turns = 1.6 + rnd() * 1.6
    const r0 = 3 + rnd() * 3
    const r1 = r0 + 7 + rnd() * 8
    const pts: string[] = []
    for (let k = 0; k <= 40; k++) {
      const t = k / 40
      const a = t * turns * Math.PI * 2 + i
      const rr = r0 + (r1 - r0) * t
      pts.push(`${(cx + Math.cos(a) * rr).toFixed(1)},${(cy + Math.sin(a) * rr * 0.62).toFixed(1)}`)
    }
    curls.push(`M${pts.join('L')}`)
  }
  return (
    <Face>
      {curls.map((d, i) => (
        <g key={i}>
          <path d={d} fill="none" stroke="#6b7280" strokeWidth={2.6} strokeLinecap="round" />
          <path d={d} fill="none" stroke="#e5e7eb" strokeWidth={1.1} strokeLinecap="round" />
        </g>
      ))}
    </Face>
  )
}

export function TinArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 80
  const h = p.h ?? 180
  return (
    <Drum r={r} h={h} side={band('silver')}>
      <circle r={r} fill="url(#sheen)" />
      <circle r={r * 0.9} fill="none" stroke="#6b7280" strokeWidth={r * 0.03} />
      <circle r={r * 0.82} fill="url(#sheen)" opacity={0.85} />
      <circle r={r * 0.46} fill={p.color} stroke="#ffffff" strokeOpacity={0.5} strokeWidth={1.2} />
      <ellipse cx={-r * 0.14} cy={-r * 0.16} rx={r * 0.18} ry={r * 0.1} fill="#ffffff" opacity={0.25} />
      <text y={r * 0.66} textAnchor="middle" fontSize={r * 0.16} fontWeight={800} fill="#1f2937">
        {p.text}
      </text>
      <text y={-r * 0.56} textAnchor="middle" fontSize={r * 0.12} fill="#374151">
        {p.sub}
      </text>
    </Drum>
  )
}

export function DecalSheetArt({ p }: { p: ArtParams }) {
  const w = p.w ?? 380
  const h = p.h ?? 200
  return (
    <Face>
      <rect width={w} height={h} rx={8} fill="#eef4fb" opacity={0.95} />
      <rect width={w} height={h} rx={8} fill="none" stroke="#9fb3c8" strokeWidth={1.2} />
      <text x={14} y={24} fontSize={12} fontWeight={700} letterSpacing={2} fill="#475569">
        DECAL SET · {p.text}
      </text>
      <g fill="none" stroke="#94a3b8" strokeDasharray="4 3" strokeWidth={1}>
        <rect x={20} y={27} width={Math.min(338, 48 + (p.text?.length ?? 6) * 27)} height={26} rx={4} />
        <circle cx={60} cy={130} r={25} />
        <circle cx={150} cy={130} r={20} />
        <rect x={206} y={133} width={158} height={14} rx={3} />
      </g>
    </Face>
  )
}

export function DecalArt({ p }: { p: ArtParams }) {
  const text = p.text ?? ''
  if (p.which === 'badge') {
    const r = (p.w ?? 40) / 2
    return (
      <Face>
        <circle r={r} fill="#0f172a" stroke="#e5e9ef" strokeWidth={r * 0.12} />
        <circle r={r * 0.68} fill="none" stroke="#67e8f9" strokeWidth={r * 0.06} />
        <text y={r * 0.36} textAnchor="middle" fontSize={r * 1.05} fontWeight={800} fill="#e5e9ef">
          {text}
        </text>
      </Face>
    )
  }
  if (p.which === 'stripe') {
    const w = p.w ?? 150
    return (
      <Face>
        <rect y={-1.4} width={w} height={2.8} rx={1.4} fill="#67e8f9" opacity={0.9} />
        <text x={w} y={-4} textAnchor="end" fontSize={6.5} letterSpacing={1.5} fill="#cbd5e1">
          {text}
        </text>
      </Face>
    )
  }
  const w = p.w ?? 200
  const h = p.h ?? 22
  return (
    <Face>
      <text x={0} y={h * 0.36} textLength={w} lengthAdjust="spacingAndGlyphs" fontSize={h * 1.05} fontWeight={800} letterSpacing={4} fill="#eef2f6" stroke="#0f172a" strokeWidth={0.7} paintOrder="stroke">
        {text}
      </text>
      <rect x={0} y={h * 0.56} width={w * 0.28} height={2.2} fill="#67e8f9" />
    </Face>
  )
}

export function HeadsetBearingArt({ p }: { p: ArtParams }) {
  const o = p.outer ?? 21
  const i = p.inner ?? 15
  return (
    <Face>
      <circle r={o} fill="url(#sheen)" />
      <circle r={o - 2.4} fill="#111317" />
      <circle r={(o + i) / 2} fill="none" stroke="#3b82f6" strokeWidth={0.8} strokeOpacity={0.5} />
      <circle r={i + 2.2} fill="url(#sheen)" />
      <circle r={i} fill="#05060a" />
    </Face>
  )
}

export function BoltArt({ p }: { p: ArtParams }) {
  const [hd, hl] = p.head ?? [8.5, 5]
  const [sd, sl] = p.shank ?? [5, 16]
  const finish = band(p.finish ?? 'steel')
  const which = p.which ?? 'socket'
  const cap =
    which === 'socket' ? (
      <>
        <circle r={1} fill="url(#cap-shine)" />
        <path d="M0.42,0 L0.21,0.36 L-0.21,0.36 L-0.42,0 L-0.21,-0.36 L0.21,-0.36 Z" fill="#0b0c0f" />
      </>
    ) : which === 'insert' || which === 'ferrule' ? (
      <>
        <circle r={1} fill="url(#cap-shine)" />
        <circle r={0.55} fill="#0b0c0f" />
      </>
    ) : (
      <>
        <circle r={1} fill="url(#cap-shine)" />
        <path d="M0.3,0 L0.15,0.26 L-0.15,0.26 L-0.3,0 L-0.15,-0.26 L0.15,-0.26 Z" fill="#0b0c0f" />
      </>
    )
  return (
    <>
      <Bar a={[hl, 0, 0]} b={[hl + sl, 0, 0]} r={sd / 2} fill={band('steel')} round={0.1} cap={false} style={fade} />
      <Bar a={[hl, 0, 0]} b={[0, 0, 0]} r={hd / 2} fill={finish} round={which === 'button' ? 0.9 : 0.25} cap={cap} />
    </>
  )
}

export function BbShellArt({ p }: { p: ArtParams }) {
  const o = p.outer ?? 21
  const i = p.inner ?? 17.5
  return (
    <Drum r={o} h={p.depth ?? 68} side={band('alloy')}>
      <circle r={o} fill="url(#sheen)" />
      <circle r={o} fill={band('paint')} style={coatStyle('--paint')} />
      <circle r={i} fill="#0c0e12" />
      <circle r={(o + i) / 2} fill="none" stroke="#ffffff" strokeOpacity={0.25} strokeWidth={0.6} strokeDasharray="1.2 1.6" />
    </Drum>
  )
}

export function BearingArt({ p }: { p: ArtParams }) {
  const o = p.outer ?? 13
  const i = p.inner ?? 8.5
  return (
    <Face>
      <circle r={o} fill="url(#sheen)" />
      <circle r={o - 1.6} fill="#16181d" />
      <circle r={(o + i) / 2} fill="none" stroke="#ef4444" strokeOpacity={0.55} strokeWidth={0.5} />
      <circle r={i + 1.5} fill="url(#sheen)" />
      <circle r={i} fill="#05060a" />
    </Face>
  )
}

export function DropoutArt({ p }: { p: ArtParams }) {
  const right = (p.side ?? 1) > 0
  const d = right
    ? 'M-34,-28 C-12,-44 26,-46 44,-26 L52,-6 C56,8 50,18 40,22 L24,30 L18,52 C16,60 6,62 2,56 L-6,30 C-24,22 -40,6 -40,-10 Z'
    : 'M-36,-26 C-14,-44 28,-44 46,-24 L54,-2 C56,12 46,22 32,24 L-4,26 C-24,24 -40,8 -40,-8 Z'
  return (
    <Face>
      <path d={d} fill="url(#sheen)" />
      <path d={d} fill="url(#sheen-dark)" style={coatStyle('--anod')} />
      <path d={d} fill="none" stroke="#ffffff" strokeOpacity={0.18} strokeWidth={1} />
      <circle r={7} fill="#05060a" />
      {right && <circle cx={10} cy={46} r={3.4} fill="#05060a" />}
      <circle cx={-22} cy={-14} r={2.6} fill="#05060a" />
    </Face>
  )
}

export function BracketArt() {
  const d = 'M-30,-10 C-30,-16 -26,-16 -20,-16 H20 C26,-16 30,-16 30,-10 V8 C30,14 26,16 20,16 H-20 C-26,16 -30,14 -30,8 Z'
  return (
    <Face>
      <path d={d} fill="url(#sheen)" />
      <path d={d} fill="url(#sheen-paint)" style={coatStyle('--paint')} />
      <circle cx={-16} r={4} fill="#05060a" />
      <circle cx={16} r={4} fill="#05060a" />
    </Face>
  )
}

export function WasherArt({ p }: { p: ArtParams }) {
  return (
    <Face>
      <circle r={p.r ?? 6} fill="url(#sheen)" />
      <circle r={p.inner ?? 3.2} fill="#05060a" />
    </Face>
  )
}

export function NutArt({ p }: { p: ArtParams }) {
  const r = (p.r ?? 6) * 1.1
  const hex = [...Array(6).keys()].map((k) => `${(Math.cos((k * Math.PI) / 3) * r).toFixed(2)},${(Math.sin((k * Math.PI) / 3) * r).toFixed(2)}`).join(' ')
  return (
    <Face>
      <polygon points={hex} fill="url(#sheen)" />
      <circle r={r * 0.72} fill="#e2e8f0" />
      <circle r={p.inner ?? 3.2} fill="#05060a" />
    </Face>
  )
}
