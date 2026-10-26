import { DEPTH, rotX } from '../projection'
import type { ArtParams } from '../catalog'
import { polylineLength, sprocketPath, type P2 } from '../geometry'
import { band } from './defs'
import { Bar, Face } from './rig'

// Cockpit, drivetrain, brakes and cables, saddle, seatpost and pedals.

const darkCap = <circle r={1} fill="url(#cap-dark)" />

export function HandlebarArt({ p }: { p: ArtParams }) {
  const half = (p.length ?? 420) / 2
  const r = (p.diameter ?? 22.2) / 2
  const R = (p.outer ?? 31.8) / 2
  return (
    <>
      <Bar a={[0, 0, -40]} b={[0, 0, -half]} r={r} fill={band('alloy')} cap={darkCap} />
      <Bar a={[0, 0, -40]} b={[0, 0, 40]} r={R} fill={band('alloy')} cap={false} />
      <Bar a={[0, 0, 40]} b={[0, 0, half]} r={r} fill={band('alloy')} cap={darkCap} />
    </>
  )
}

export function StemArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 100
  const a = p.r ?? 0.43
  const h = p.outer ?? 1.89
  const end = [Math.cos(a) * L, Math.sin(a) * L] as const
  const hd = [Math.cos(h), Math.sin(h)] as const
  return (
    <>
      <Bar a={[-hd[0] * 22, -hd[1] * 22, 0]} b={[hd[0] * 22, hd[1] * 22, 0]} r={20} fill={band('black')} cap={darkCap} />
      <Bar a={[0, 0, 0]} b={[end[0], end[1], 0]} r={16} fill={band('black')} cap={false} />
      <Bar a={[end[0], end[1], -27]} b={[end[0], end[1], 27]} r={19.5} fill={band('black')} cap={<><circle r={1} fill="url(#cap-dark)" /><circle r={0.78} fill="none" stroke="#8b939e" strokeWidth={0.08} /></>} />
    </>
  )
}

export function GripArt({ p }: { p: ArtParams }) {
  const s = p.side ?? 1
  const half = p.r ?? 210
  const z0 = s > 0 ? half - (p.length ?? 130) : -half + (p.length ?? 130)
  const z1 = s > 0 ? half : -half
  return (
    <>
      <Bar a={[0, 0, z0]} b={[0, 0, z1]} r={16} fill={band('rubber')} cap={<><circle r={1} fill="#141518" /><circle r={0.55} fill="#26282d" /></>} />
      <Bar a={[0, 0, z0]} b={[0, 0, z0 + s * 9]} r={17.5} fill={band('silver')} cap={false} />
    </>
  )
}

export function ShifterArt({ p }: { p: ArtParams }) {
  const half = p.r ?? 210
  return (
    <>
      <Bar a={[0, 0, half - 156]} b={[0, 0, half - 140]} r={13.5} fill={band('black')} cap={false} />
      <Bar a={[-4, -24, half - 188]} b={[-4, -24, half - 140]} r={13} fill={band('black')} cap={darkCap} />
      <Bar a={[-4, -24, half - 152]} b={[26, -50, half - 152]} r={4} fill={band('silver')} cap={false} />
      <Bar a={[-4, -24, half - 170]} b={[22, -40, half - 170]} r={3.4} fill={band('alloy')} cap={false} />
    </>
  )
}

export function BellArt() {
  return (
    <>
      <Bar a={[0, 0, -7]} b={[0, 0, 7]} r={13.5} fill={band('silver')} cap={false} />
      <Bar a={[0, 12, 0]} b={[0, 34, 0]} r={27} fill={band('silver')} round={0.6} cap={<><circle r={1} fill="url(#cap-shine)" /><circle r={0.18} fill="#9aa3ad" /></>} />
      <Bar a={[18, 16, 10]} b={[34, 12, 18]} r={3.2} fill={band('steel')} cap={false} />
    </>
  )
}

function armPath(L: number) {
  return `M0,-17 L${L},-10 A10,10 0 0 1 ${L},10 L0,17 A17,17 0 0 1 0,-17 Z`
}

export function CranksetArt({ p }: { p: ArtParams }) {
  const [big, small] = p.teeth ?? [50, 34]
  const L = p.length ?? 172.5
  const a = ((p.r ?? -0.31) * 180) / Math.PI
  const [zBig, zSmall] = p.zs ?? [52, 45]
  const spider = [...Array(4).keys()].map((k) => {
    const t = (k / 4) * Math.PI * 2 + 0.4
    return `M0,0 L${Math.cos(t - 0.16) * 74},${Math.sin(t - 0.16) * 74} L${Math.cos(t + 0.16) * 74},${Math.sin(t + 0.16) * 74} Z`
  })
  return (
    <>
      <Face z={-80}>
        <g transform={`rotate(${-a + 180})`}>
          <path d={armPath(L)} fill="url(#sheen-dark)" />
          <circle cx={L} r={6} fill="#07080b" />
        </g>
      </Face>
      <Bar a={[0, 0, -80]} b={[0, 0, 80]} r={12} fill={band('steel')} cap={false} />
      <Face z={zSmall}>
        <path d={sprocketPath(small, 50)} fillRule="evenodd" fill="url(#sheen-dark)" stroke="#555c66" strokeWidth={0.6} />
      </Face>
      <Face z={zBig}>
        <path d={sprocketPath(big, 82)} fillRule="evenodd" fill="url(#sheen)" stroke="#4b525c" strokeWidth={0.6} />
        <circle r={86} fill="none" stroke="#1f2329" strokeWidth={3} />
      </Face>
      <Face z={zBig + 4}>
        {spider.map((d, k) => (
          <path key={k} d={d} fill="url(#sheen-dark)" />
        ))}
        <circle r={24} fill="url(#sheen-dark)" />
      </Face>
      <Face z={80}>
        <g transform={`rotate(${-a})`}>
          <path d={armPath(L)} fill="url(#sheen-dark)" />
          <path d={`M14,-9 L${L - 8},-5`} stroke="#ffffff" strokeOpacity={0.18} strokeWidth={2} strokeLinecap="round" />
          <circle cx={L} r={6} fill="#07080b" />
        </g>
        <circle r={13} fill="url(#cap-dark)" stroke="#6b7280" strokeWidth={1} />
        <circle r={5} fill="#05060a" />
      </Face>
    </>
  )
}

export function ChainArt({ p }: { p: ArtParams }) {
  const pts = p.points ?? []
  const d = pts.map((q, i) => `${i ? 'L' : 'M'}${q[0].toFixed(1)},${(-q[1]).toFixed(1)}`).join('') + 'Z'
  const pitch = polylineLength(pts) / Math.max(1, p.holes ?? 116)
  return (
    <Face>
      <path d={d} fill="none" stroke="#2b3038" strokeWidth={8} strokeLinejoin="round" />
      <path d={d} fill="none" stroke="#a7b0bb" strokeWidth={9} strokeDasharray={`${(pitch * 1.02).toFixed(2)} ${(pitch * 0.98).toFixed(2)}`} />
      <path d={d} fill="none" stroke="#5a626d" strokeWidth={3.2} />
      <path d={d} fill="none" stroke="#e5e9ee" strokeWidth={3.6} strokeLinecap="round" strokeDasharray={`0.1 ${(pitch - 0.1).toFixed(2)}`} />
    </Face>
  )
}

export function QuickLinkArt() {
  return (
    <Face>
      <rect x={-13} y={-5.4} width={26} height={10.8} rx={5.4} fill={band('gold')} />
      <circle cx={-6.4} r={2.3} fill="#5b4210" />
      <circle cx={6.4} r={2.3} fill="#5b4210" />
    </Face>
  )
}

export function DerailleurArt({ p }: { p: ArtParams }) {
  const [up, low] = (p.points ?? [
    [13, -72],
    [-12, -142],
  ]) as P2[]
  const U = [up[0], -up[1]]
  const L = [low[0], -low[1]]
  const ang = (Math.atan2(L[1] - U[1], L[0] - U[0]) * 180) / Math.PI
  const len = Math.hypot(L[0] - U[0], L[1] - U[1])
  const jockey = sprocketPath(11, 4)
  return (
    <>
      <Face z={55}>
        <g transform={`translate(${U[0]} ${U[1]}) rotate(${ang})`}>
          <rect x={-22} y={-17} width={len + 44} height={34} rx={17} fill="url(#sheen-dark)" stroke="#4b525c" strokeWidth={0.8} />
        </g>
        <path d={jockey} transform={`translate(${U[0]} ${U[1]})`} fill="url(#sheen)" />
        <path d={jockey} transform={`translate(${L[0]} ${L[1]})`} fill="url(#sheen)" />
        <circle cx={U[0]} cy={U[1]} r={5} fill="#0b0c0f" />
        <circle cx={L[0]} cy={L[1]} r={5} fill="#0b0c0f" />
      </Face>
      <Face z={64}>
        <path d={`M6,40 L30,48 L${U[0] + 18},${U[1] - 22} L${U[0] - 6},${U[1] - 30} Z`} fill="url(#sheen-dark)" stroke="#59606b" strokeWidth={1} />
        <rect x={U[0] - 10} y={U[1] - 36} width={30} height={18} rx={6} fill="url(#sheen-dark)" />
        <circle cx={10} cy={46} r={8} fill="url(#cap-dark)" />
        <circle cx={10} cy={46} r={3} fill="#9aa3ad" />
        <circle cx={U[0] + 8} cy={U[1] - 27} r={3} fill="#9aa3ad" />
        <path d={`M14,40 L${U[0] + 10},${U[1] - 18}`} stroke="#fb923c" strokeOpacity={0.8} strokeWidth={1.6} />
      </Face>
    </>
  )
}

export function CaliperArt() {
  const body = 'M-36,-4 C-36,-20 -26,-24 -14,-24 H14 C26,-24 36,-20 36,-4 V6 C36,14 30,18 22,18 H-22 C-30,18 -36,14 -36,6 Z'
  return (
    <Face>
      <path d={body} fill="url(#sheen-dark)" stroke="#4b525c" strokeWidth={1} />
      <rect x={-30} y={10} width={60} height={4} rx={2} fill="#020304" />
      <ellipse cx={-14} cy={-7} rx={11} ry={8} fill="#2a2f37" />
      <ellipse cx={14} cy={-7} rx={11} ry={8} fill="#2a2f37" />
      <circle cx={-30} cy={-18} r={5} fill="url(#cap-shine)" />
      <circle cx={30} cy={-18} r={3} fill="#9aa3ad" />
    </Face>
  )
}

export function CoilArt({ p }: { p: ArtParams }) {
  const r = p.r ?? 80
  const w = Math.max(1.4, (p.w ?? 4) * 1.15)
  const color = p.color ?? '#111'
  const light = p.which === 'cable'
  return (
    <Face>
      {[0, 1, 2, 3].map((k) => (
        <ellipse key={k} cx={k * 2.4} cy={k * 1.6} rx={r - k * 6} ry={(r - k * 6) * 0.92} fill="none" stroke={light ? '#c7ced8' : color} strokeWidth={w} />
      ))}
      {!light && [0, 1, 2, 3].map((k) => <ellipse key={`h${k}`} cx={k * 2.4} cy={k * 1.6} rx={r - k * 6} ry={(r - k * 6) * 0.92} fill="none" stroke="#ffffff" strokeOpacity={0.08} strokeWidth={w * 0.35} />)}
      <path d={`M${r * 0.7},${r * 0.7} q${r * 0.25},${r * 0.1} ${r * 0.34},${r * 0.3}`} fill="none" stroke={light ? '#c7ced8' : color} strokeWidth={w} strokeLinecap="round" />
      {p.text && (
        <g transform={`translate(${-r * 0.42} ${-r * 0.08}) rotate(-8)`}>
          <rect width={r * 0.84} height={22} rx={4} fill="#f8fafc" />
          <text x={r * 0.42} y={15} textAnchor="middle" fontSize={12} fontWeight={700} fill="#0f172a">
            {p.text}
          </text>
        </g>
      )}
    </Face>
  )
}

/** A cable or hose run (bike coordinates projected to the drawing), drawing itself on as it is routed. */
export function RouteArt({ p }: { p: ArtParams }) {
  const pts = (p.points ?? []).map((q, i) => {
    const z = p.zs?.[i] ?? 0
    return [q[0] + z * DEPTH.x, -q[1] + z * DEPTH.y] as const
  })
  let d = pts.length ? `M${pts[0][0].toFixed(1)},${pts[0][1].toFixed(1)}` : ''
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[Math.max(0, i - 1)]
    const p1 = pts[i]
    const p2 = pts[i + 1]
    const p3 = pts[Math.min(pts.length - 1, i + 2)]
    const c1 = [p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6]
    const c2 = [p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6]
    d += `C${c1[0].toFixed(1)},${c1[1].toFixed(1)} ${c2[0].toFixed(1)},${c2[1].toFixed(1)} ${p2[0].toFixed(1)},${p2[1].toFixed(1)}`
  }
  const draw = { strokeDashoffset: 'calc(1 - var(--m, 1))' }
  return (
    <Face>
      <path d={d} fill="none" stroke={p.color} strokeWidth={p.w ?? 4} strokeLinecap="round" pathLength={1} strokeDasharray="1 1" style={draw} />
      {p.which !== 'cable' && <path d={d} fill="none" stroke="#ffffff" strokeOpacity={0.12} strokeWidth={(p.w ?? 4) * 0.3} strokeLinecap="round" pathLength={1} strokeDasharray="1 1" style={draw} />}
    </Face>
  )
}

export function SaddleArt() {
  const shell = 'M-138,-6 C-142,-26 -110,-36 -60,-34 C-10,-32 40,-24 92,-14 C118,-9 138,-6 140,0 C138,8 110,10 80,8 C30,6 -40,10 -96,14 C-124,16 -136,10 -138,-6 Z'
  return (
    <Face>
      <path d="M-104,14 C-80,34 -30,44 18,40 C50,37 80,22 100,8" fill="none" stroke={band('silver')} strokeWidth={6} />
      <path d="M-104,14 C-80,34 -30,44 18,40 C50,37 80,22 100,8" fill="none" stroke="#c7ced8" strokeWidth={2} />
      <path d={shell} fill="url(#sheen-dark)" stroke="#3b414b" strokeWidth={1} />
      <path d="M-128,-12 C-100,-28 -40,-28 20,-22 C60,-18 100,-10 130,-4" fill="none" stroke="#9aa3ad" strokeOpacity={0.45} strokeWidth={1} strokeDasharray="3 3" />
      <path d="M-120,-20 C-90,-30 -40,-30 0,-27" fill="none" stroke="#ffffff" strokeOpacity={0.12} strokeWidth={5} strokeLinecap="round" />
    </Face>
  )
}

export function SeatpostArt({ p }: { p: ArtParams }) {
  const L = p.length ?? 350
  const r = (p.diameter ?? 27.2) / 2
  return (
    <>
      <Bar a={[0, 0, 0]} b={[L, 0, 0]} r={r} fill={band('black')} round={0.15} cap={false} />
      <Face>
        <path d={`M${L - 6},${-r - 9} h26 q8,0 8,8 v${2 * r + 2} q0,8 -8,8 h-26 Z`} fill="url(#sheen-dark)" />
        <circle cx={L + 12} cy={-r - 2} r={3.4} fill="url(#cap-shine)" />
        <circle cx={L + 12} cy={r + 2} r={3.4} fill="url(#cap-shine)" />
        <text x={86} y={3} fontSize={7} fontWeight={700} letterSpacing={1.2} fill="#9aa3ad">
          MIN INSERT
        </text>
        <rect x={80} y={-r} width={1.4} height={2 * r} fill="#9aa3ad" />
      </Face>
    </>
  )
}

export function PedalArt({ p }: { p: ArtParams }) {
  const s = p.side ?? 1
  const pins = [-38, -13, 13, 38].flatMap((x) => [-44, 44].map((y) => [x, y] as const)).concat([-44, 44].flatMap((x) => [-14, 14].map((y) => [x, y] as const)))
  const spindle = <Bar a={[0, 0, 0]} b={[0, 0, s * 32]} r={7} fill={band('steel')} cap={false} />
  const platform = (
    <Face at={[0, 0, s * 84]} r={rotX(-Math.PI / 2)}>
      <rect x={-50} y={-52} width={100} height={104} rx={16} fill="url(#sheen-dark)" stroke="#4b525c" strokeWidth={1.2} />
      <rect x={-30} y={-26} width={60} height={52} rx={10} fill="#07080b" />
      <rect x={-6} y={-52} width={12} height={104} fill="#23272e" />
      {pins.map(([x, y], k) => (
        <circle key={k} cx={x} cy={y} r={2.6} fill="#c7ced8" />
      ))}
    </Face>
  )
  return s > 0 ? (
    <>
      {spindle}
      {platform}
    </>
  ) : (
    <>
      {platform}
      {spindle}
    </>
  )
}
