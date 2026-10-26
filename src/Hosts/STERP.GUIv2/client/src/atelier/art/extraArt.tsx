import { DEPTH } from '../projection'
import type { ArtParams } from '../catalog'
import { band } from './defs'
import { Bar, Block, Face } from './rig'

// Accessories, consumables, packaging, job tickets, the work instruction and the generic part tile.

const arc = (r: number, a0: number, a1: number, z = 0) => {
  const out: [number, number][] = []
  const n = Math.max(2, Math.ceil(Math.abs(a1 - a0) / 4))
  for (let i = 0; i <= n; i++) {
    const a = ((a0 + ((a1 - a0) * i) / n) * Math.PI) / 180
    out.push([Math.cos(a) * r + z * DEPTH.x, -Math.sin(a) * r + z * DEPTH.y])
  }
  return out
}
const poly = (pts: [number, number][]) => pts.map((q) => `${q[0].toFixed(1)},${q[1].toFixed(1)}`).join(' ')

export function MudguardArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 358
  const [a0, a1] = p.span ?? [0, 160]
  const depth = p.depth ?? 45
  const far = arc(r, a0, a1)
  const near = arc(r, a0, a1, depth)
  const stay = (a: number, z: number) => {
    const t = (a * Math.PI) / 180
    return `M${z * DEPTH.x},${z * DEPTH.y} L${Math.cos(t) * (r - 4) + z * DEPTH.x},${-Math.sin(t) * (r - 4) + z * DEPTH.y}`
  }
  const stays = [a0 + 24, a1 - 32]
  return (
    <Face>
      {stays.map((a) => (
        <path key={`f${a}`} d={stay(a, 0)} stroke="#6b7280" strokeWidth={3} />
      ))}
      <polygon points={poly([...far, ...near.reverse()])} fill="#1b1e24" />
      <polyline points={poly(far)} fill="none" stroke="#0a0b0e" strokeWidth={4} />
      <polyline points={poly(arc(r, a0, a1, depth))} fill="none" stroke="#3b414c" strokeWidth={3.4} />
      <polyline points={poly(arc(r - 2, a0 + 8, a0 + (a1 - a0) * 0.55, depth * 0.55))} fill="none" stroke="#ffffff" strokeOpacity={0.12} strokeWidth={3} />
      {stays.map((a) => (
        <path key={`n${a}`} d={stay(a, depth)} stroke={band('silver')} strokeWidth={3.2} />
      ))}
    </Face>
  )
}

export function RackArt({ p }: { p: ArtParams }) {
  const h = p.h ?? 386
  const w = p.w ?? 72
  const [eyelet, arm] = p.points ?? [
    [-28, -24],
    [120, 300],
  ]
  const side = (
    <>
      <path d={`M-252,${-h} L110,${-h} L${arm[0]},${-arm[1]}`} fill="none" stroke="#5f6773" strokeWidth={9} strokeLinejoin="round" />
      <path d={`M-214,${-h} L${eyelet[0]},${-eyelet[1]} M-70,${-h} L${eyelet[0] + 6},${-eyelet[1] - 34}`} fill="none" stroke="#5f6773" strokeWidth={8} />
    </>
  )
  return (
    <>
      <Face z={-w}>{side}</Face>
      {[-236, -160, -84, -8, 68].map((x) => (
        <Bar key={x} a={[x, h, -w]} b={[x, h, w]} r={4.4} fill={band('silver')} cap={false} />
      ))}
      <Bar a={[-252, h, -w]} b={[-252, h, w]} r={6} fill={band('silver')} cap={false} />
      <Face z={w}>
        <path d={`M-252,${-h} L110,${-h} L${arm[0]},${-arm[1]}`} fill="none" stroke={band('silver')} strokeWidth={10} strokeLinejoin="round" />
        <path d={`M-214,${-h} L${eyelet[0]},${-eyelet[1]} M-70,${-h} L${eyelet[0] + 6},${-eyelet[1] - 34}`} fill="none" stroke={band('silver')} strokeWidth={9} />
        <path d={`M-246,${-h - 3} L104,${-h - 3}`} stroke="#ffffff" strokeOpacity={0.35} strokeWidth={1.6} />
        <circle cx={eyelet[0]} cy={-eyelet[1]} r={7} fill="url(#cap-shine)" />
      </Face>
    </>
  )
}

export function LampArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 80
  const r = p.r ?? 16
  const front = p.which !== 'rear'
  const lens = front ? '#fff7d6' : '#ef4444'
  return (
    <>
      {front && (
        <Face>
            <polygon points={`${L},${-r * 0.7} ${L + 900},-230 ${L + 900},230 ${L},${r * 0.7}`} fill="url(#beam)" style={{ opacity: 'var(--lit, 0)' }} />
          </Face>
      )}
      <Bar a={[L * 0.45, -r, 0]} b={[L * 0.45, -r - 16, 0]} r={5} fill={band('black')} cap={false} />
      <Bar
        a={[0, 0, 0]}
        b={[L, 0, 0]}
        r={r}
        fill={band('black')}
        round={0.45}
        cap={
          <>
            <circle r={1} fill={lens} />
            <circle r={0.62} fill="#ffffff" opacity={front ? 0.6 : 0.25} />
            <circle r={3.2} fill={front ? 'url(#glow-warm)' : 'url(#glow-red)'} style={{ opacity: 'var(--lit, 0)' }} />
          </>
        }
      />
    </>
  )
}

export function KickstandArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 330
  return (
    <Face>
      <path d={`M0,-8 L${L},-5 L${L},5 L0,8 Z`} fill={band('alloy')} />
      <rect x={L - 6} y={-8} width={28} height={16} rx={7} fill="#121316" />
      <rect x={-24} y={-15} width={48} height={30} rx={8} fill="url(#sheen)" />
      <circle cx={-12} r={3.2} fill="#05060a" />
      <circle cx={12} r={3.2} fill="#05060a" />
    </Face>
  )
}

const REFLECT: Record<string, [string, string]> = {
  white: ['#f8fafc', '#94a3b8'],
  red: ['#ef4444', '#7f1d1d'],
  amber: ['#f59e0b', '#78350f'],
}

export function ReflectorArt({ p }: { p: ArtParams }) {
  const [fill, rim] = REFLECT[p.which ?? 'amber'] ?? REFLECT.amber
  return (
    <Face>
      <rect x={-20} y={-12} width={40} height={24} rx={7} fill={rim} />
      <rect x={-17} y={-9.5} width={34} height={19} rx={5} fill={fill} />
      <path d="M-14,-6 l6,12 M-6,-6 l6,12 M2,-6 l6,12 M10,-6 l4,8" stroke="#ffffff" strokeOpacity={0.45} strokeWidth={1} />
      <circle r={9} fill={fill} style={{ opacity: 'calc(var(--lit, 0) * 0.6)' }} />
    </Face>
  )
}

export function CageArt() {
  const d = 'M0,0 L0,-14 C0,-52 12,-72 34,-76 L118,-76 C140,-72 152,-52 152,-14 L152,0 M24,-6 C30,-40 50,-58 76,-58 C102,-58 122,-40 128,-6'
  return (
    <>
      <Face z={-18}>
        <path d={d} fill="none" stroke="#6b7280" strokeWidth={4.6} strokeLinecap="round" />
      </Face>
      <Face z={18}>
        <path d={d} fill="none" stroke={band('silver')} strokeWidth={5.4} strokeLinecap="round" />
        <path d={d} fill="none" stroke="#ffffff" strokeOpacity={0.3} strokeWidth={1.4} strokeLinecap="round" />
        <circle cx={0} cy={0} r={5} fill="url(#cap-shine)" />
        <circle cx={130} cy={0} r={5} fill="url(#cap-shine)" />
      </Face>
    </>
  )
}

export function FoamArt({ p }: { p: ArtParams }) {
  const w = p.w ?? 110
  const h = p.h ?? 60
  return (
    <Block w={w} h={h} depth={p.depth ?? 40} side="#aeb6c2" rx={10}>
      <rect x={-w / 2} y={-h / 2} width={w} height={h} rx={10} fill="#eef1f5" />
      {[...Array(14).keys()].map((k) => (
        <circle key={k} cx={-w / 2 + 10 + ((k * 37) % (w - 20))} cy={-h / 2 + 10 + ((k * 23) % (h - 20))} r={1.6} fill="#c3cad5" />
      ))}
    </Block>
  )
}

export function CartonArt({ p }: { p: ArtParams }) {
  const w = p.w ?? 1480
  const h = p.h ?? 800
  const [bw, bh] = (p.points?.[0] ?? [1900, 1200]) as [number, number]
  const depth = p.depth ?? 280
  const ox = depth * DEPTH.x
  const oy = depth * DEPTH.y
  const corners: [number, number][] = [
    [-bw / 2, -bh / 2],
    [bw / 2, -bh / 2],
    [bw / 2, bh / 2],
    [-bw / 2, bh / 2],
  ]
  return (
    <>
      <g style={{ opacity: 'calc(1 - var(--m, 0) * 1.6)' }}>
        <Face>
          <rect x={-w / 2} y={-h / 2} width={w} height={h} rx={6} fill={band('cardboard')} />
          {[-0.3, 0.1, 0.38].map((k) => (
            <line key={k} x1={w * k} x2={w * k} y1={-h / 2} y2={h / 2} stroke="#7a5630" strokeOpacity={0.45} strokeWidth={2} strokeDasharray="14 10" />
          ))}
          <text x={-w * 0.43} y={-h * 0.18} fontSize={78} fontWeight={800} letterSpacing={14} fill="#3b2a17" opacity={0.75}>
            {p.text}
          </text>
          <g transform={`translate(${-w * 0.3} ${h * 0.14})`} fill="none" stroke="#3b2a17" strokeOpacity={0.7} strokeWidth={6}>
            <circle cx={-70} cy={40} r={56} />
            <circle cx={110} cy={40} r={56} />
            <path d="M-70,40 L0,-20 L80,-20 L110,40 M0,-20 L20,40 L-70,40 M80,-20 L90,-46 M-6,-40 L14,-40" />
          </g>
          <g transform={`translate(${w * 0.24} ${h * 0.18})`} fill="#3b2a17" opacity={0.7}>
            <path d="M0,-40 l18,26 h-11 v38 h-14 v-38 h-11 Z M46,-40 l18,26 h-11 v38 h-14 v-38 h-11 Z" />
            <text x={-6} y={60} fontSize={22} fontWeight={700} letterSpacing={3}>
              THIS WAY UP
            </text>
          </g>
        </Face>
      </g>
      <g style={{ opacity: 'calc(var(--m, 1) * 1.6 - 0.6)' }}>
        <Face>
          <rect x={-bw / 2} y={-bh / 2} width={bw} height={bh} rx={10} fill="#c9a170" fillOpacity={0.05} stroke="#d9b88a" strokeOpacity={0.35} strokeWidth={3} strokeDasharray="18 12" />
          {corners.map(([x, y], k) => (
            <line key={k} x1={x} y1={y} x2={x + ox} y2={y + oy} stroke="#d9b88a" strokeOpacity={0.4} strokeWidth={3} strokeDasharray="10 10" />
          ))}
        </Face>
        <Face z={depth}>
          <rect x={-bw / 2} y={-bh / 2} width={bw} height={bh} rx={10} fill="#c9a170" fillOpacity={0.06} stroke="#e7c99d" strokeOpacity={0.75} strokeWidth={4} />
          <text x={-bw / 2 + 40} y={-bh / 2 + 80} fontSize={54} fontWeight={800} letterSpacing={12} fill="#e7c99d" opacity={0.5}>
            {p.text}
          </text>
          <text x={-bw / 2 + 42} y={-bh / 2 + 118} fontSize={20} letterSpacing={4} fill="#e7c99d" opacity={0.45}>
            COMPLETE BICYCLE · HANDLE WITH CARE
          </text>
        </Face>
      </g>
    </>
  )
}

export function LabelArt({ p }: { p: ArtParams }) {
  const w = p.w ?? 105
  const h = p.h ?? 148
  const bars = [...Array(34).keys()].map((k) => ({ x: 10 + k * 2.5, w: (k * 7) % 3 === 0 ? 1.6 : 0.8 }))
  return (
    <Face>
      <rect width={w} height={h} rx={3} fill="#fbfbf8" />
      <rect width={w} height={18} rx={3} fill="#0f172a" />
      <text x={8} y={13} fontSize={9} fontWeight={800} letterSpacing={1.5} fill="#f8fafc">
        SHIP TO
      </text>
      {[30, 40, 50, 60].map((y, k) => (
        <rect key={y} x={8} y={y} width={k === 0 ? 70 : 58 - k * 6} height={5} rx={2} fill="#9aa3ad" />
      ))}
      {bars.map((b, k) => (
        <rect key={k} x={b.x} y={84} width={b.w} height={34} fill="#0f172a" />
      ))}
      <text x={8} y={136} fontSize={8} fontWeight={700} fill="#334155">
        {p.text} · 1 OF 1
      </text>
    </Face>
  )
}

export function ManualArt({ p }: { p: ArtParams }) {
  const w = p.w ?? 148
  const h = p.h ?? 210
  return (
    <Face>
      <rect x={4} y={4} width={w} height={h} rx={4} fill="#cbd5e1" />
      <rect width={w} height={h} rx={4} fill="#0f172a" />
      <rect x={0} y={h * 0.62} width={w} height={h * 0.38} fill="#67e8f9" opacity={0.14} />
      <text x={12} y={30} fontSize={15} fontWeight={800} letterSpacing={2} fill="#e2e8f0">
        {p.text}
      </text>
      <text x={12} y={50} fontSize={10} fill="#94a3b8">
        Owner manual
      </text>
      <g transform={`translate(${w / 2} ${h * 0.47})`} fill="none" stroke="#67e8f9" strokeWidth={2.4}>
        <circle cx={-28} cy={10} r={17} />
        <circle cx={28} cy={10} r={17} />
        <path d="M-28,10 L-6,-12 L20,-12 L28,10 M-6,-12 L2,10 L-28,10" />
      </g>
      <text x={12} y={h - 34} fontSize={8} fill="#94a3b8">
        Declaration of conformity
      </text>
      <text x={w - 34} y={h - 14} fontSize={16} fontWeight={800} fill="#e2e8f0">
        CE
      </text>
    </Face>
  )
}

export function CartridgeArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 230
  const r = p.r ?? 27
  return (
    <Face>
      <rect x={0} y={-r} width={L - 20} height={2 * r} rx={6} fill={band('blue')} />
      <rect x={L * 0.2} y={-r * 0.7} width={L * 0.45} height={r * 1.4} rx={4} fill="#f8fafc" />
      <text x={L * 0.425} y={4} textAnchor="middle" fontSize={11} fontWeight={800} fill="#1e3a8a">
        {p.text}
      </text>
      <rect x={L - 22} y={-r * 0.5} width={22} height={r} rx={4} fill={band('black')} />
      <rect x={-6} y={-r * 0.8} width={8} height={r * 1.6} rx={2} fill={band('silver')} />
    </Face>
  )
}

export function DabArt() {
  return (
    <Face>
      <ellipse rx={7} ry={4.6} fill="#e0f2fe" />
      <ellipse cx={-2} cy={-1.4} rx={2.6} ry={1.4} fill="#ffffff" />
    </Face>
  )
}

const ICONS: Record<string, string> = {
  heat: 'M0,-14 C6,-6 10,-2 10,5 C10,11 5,15 0,15 C-5,15 -10,11 -10,5 C-10,-1 -4,-4 -3,-10 C0,-6 1,-4 0,-14 Z',
  anodise: 'M0,-15 C5,-7 10,-1 10,5 C10,11 5,15 0,15 C-5,15 -10,11 -10,5 C-10,-1 -5,-7 0,-15 Z',
  inspect: 'M-9,1 L-3,7 L10,-7',
  paint: 'M-8,-12 H8 V2 H-8 Z M-2,2 V14',
  service: 'M0,-12 L3,-7 L9,-8 L8,-2 L12,2 L7,5 L7,11 L1,9 L-3,13 L-5,7 L-11,6 L-8,1 L-12,-3 L-6,-5 L-6,-11 L-1,-8 Z',
}

export function TicketArt({ p }: { p: ArtParams }) {
  const tag = p.which === 'tag'
  const y0 = tag ? 34 : 0
  const icon = ICONS[p.icon ?? 'service'] ?? ICONS.service
  const stroked = p.icon === 'inspect'
  return (
    <Face>
      {tag && <path d="M0,0 C6,10 10,22 22,44" fill="none" stroke="#e2e8f0" strokeWidth={1.4} />}
      <rect x={0} y={y0} width={140} height={90} rx={9} fill="#f8fafc" />
      <rect x={0} y={y0} width={10} height={90} rx={4} fill="#34d399" />
      <circle cx={22} cy={y0 + 10} r={4.5} fill="#0b0f19" />
      <circle cx={38} cy={y0 + 44} r={16} fill="#ecfdf5" stroke="#34d399" strokeWidth={1.6} />
      <path d={icon} transform={`translate(38 ${y0 + 44})`} fill={stroked ? 'none' : '#059669'} stroke={stroked ? '#059669' : 'none'} strokeWidth={3} strokeLinecap="round" strokeLinejoin="round" />
      <text x={62} y={y0 + 36} fontSize={10.5} fontWeight={700} fill="#0f172a">
        {(p.text ?? '').slice(0, 13)}
      </text>
      <text x={62} y={y0 + 50} fontSize={10.5} fontWeight={700} fill="#0f172a">
        {(p.text ?? '').slice(13, 26)}
      </text>
      <text x={62} y={y0 + 72} fontSize={7.5} fontFamily="JetBrains Mono Variable, monospace" fill="#64748b">
        {(p.sub ?? '').slice(0, 16)}
      </text>
      <g transform={`translate(96 ${y0 + 22}) rotate(-12)`} style={{ opacity: 'calc(var(--m, 0) * 3 - 2)' }}>
        <rect x={-34} y={-11} width={68} height={22} rx={5} fill="none" stroke="#059669" strokeWidth={2.4} />
        <text textAnchor="middle" y={4.5} fontSize={11} fontWeight={800} letterSpacing={1} fill="#059669">
          {tag ? 'QC PASSED' : 'DONE'}
        </text>
      </g>
    </Face>
  )
}

export function NoteArt({ p }: { p: ArtParams }) {
  return (
    <Face>
      <g transform="rotate(-2 110 75)">
        <rect width={220} height={150} rx={4} fill="#fdf6c3" />
        <rect width={220} height={26} rx={4} fill="#fbe68a" />
        <text x={12} y={18} fontSize={10} fontWeight={800} letterSpacing={1.6} fill="#713f12">
          WORK INSTRUCTION
        </text>
        {(p.lines ?? []).map((l, k) => (
          <text key={k} x={12} y={46 + k * 16} fontSize={11} fill="#422006">
            {l}
          </text>
        ))}
        <text x={12} y={142} fontSize={8} fontFamily="JetBrains Mono Variable, monospace" fill="#a16207">
          {p.text}
        </text>
      </g>
      <circle cx={110} cy={6} r={7} fill="#ef4444" />
      <circle cx={108} cy={4} r={2.4} fill="#fecaca" />
    </Face>
  )
}

export function PartArt({ p }: { p: ArtParams }) {
  return (
    <Face>
      <rect width={180} height={90} rx={12} fill="#1e1b3a" stroke="#a78bfa" strokeOpacity={0.6} strokeWidth={2} />
      <path d="M30,24 l14,8 v16 l-14,8 l-14,-8 v-16 Z" fill="none" stroke="#c4b5fd" strokeWidth={2.4} />
      <text x={56} y={36} fontSize={10} fontFamily="JetBrains Mono Variable, monospace" fill="#ede9fe">
        {(p.text ?? '').slice(0, 18)}
      </text>
      <text x={56} y={56} fontSize={10} fill="#c4b5fd">
        {(p.sub ?? '').slice(0, 20)}
      </text>
    </Face>
  )
}
