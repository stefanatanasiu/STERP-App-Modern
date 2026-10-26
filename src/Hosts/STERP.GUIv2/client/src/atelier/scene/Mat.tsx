import { memo, type ReactNode } from 'react'
import { apply, compose, toSvg, type Pose } from '../projection'
import { CAPTION } from '../layout'
import type { ANode, AtelierModel, Box } from '../types'

// The static layer under the pieces: the cutting mat, the bay where the bike is assembled, each subassembly's cell with its
// build area and a dashed ghost of what will be built there, phantom trays, and the caption under every line on the mat.
// Headers and captions keep a readable size at any zoom (--hs and --cs are set by the camera).

const ink = { 50: '#f5f7fc', 200: '#c4cada', 300: '#9aa3ba', 400: '#7c86a0' }

function CropMarks({ b, color }: { b: Box; color: string }) {
  const l = Math.min(60, b.w / 5, b.h / 5)
  const corners: [number, number, number, number][] = [
    [b.x, b.y, 1, 1],
    [b.x + b.w, b.y, -1, 1],
    [b.x, b.y + b.h, 1, -1],
    [b.x + b.w, b.y + b.h, -1, -1],
  ]
  return (
    <g stroke={color} strokeWidth={4} strokeLinecap="round" fill="none" opacity={0.6}>
      {corners.map(([x, y, sx, sy], k) => (
        <path key={k} d={`M${x + sx * l},${y} H${x} V${y + sy * l}`} />
      ))}
    </g>
  )
}

function Header({ x, y, color, title, sub, kind }: { x: number; y: number; color: string; title: string; sub: string; kind?: string }) {
  return (
    <g className="hdr" style={{ transform: `translate(${x}px, ${y}px) scale(var(--hs, 1))` }}>
      <circle cx={9} cy={-10} r={8} fill={color} />
      <circle cx={9} cy={-10} r={15} fill={color} opacity={0.18} />
      {kind && (
        <text x={34} y={-28} fontSize={13} fontWeight={700} letterSpacing={2.4} fill={color}>
          {kind}
        </text>
      )}
      <text x={34} y={0} fontSize={30} fontWeight={650} fill={ink[50]}>
        {title}
      </text>
      <text x={34} y={26} fontSize={15} fill={ink[300]} fontFamily="JetBrains Mono Variable, monospace">
        {sub}
      </text>
    </g>
  )
}

/** A dashed outline of what a subassembly (or the bike) will look like, drawn where it is built. */
function Ghost({ model, node, at, color }: { model: AtelierModel; node: ANode; at: Pose; color: string }) {
  const lines: string[] = []
  const circles: [number, number, number][] = []
  for (const pc of model.pieces) {
    if (!pc.seat || pc.packaging || (pc.behavior !== 'fly' && pc.behavior !== 'appear')) continue
    if (!pc.node.path.split(' ').includes(node.id)) continue
    const w = compose(at, pc.seat)
    const p = pc.params ?? {}
    const seg = (a: [number, number, number], b: [number, number, number]) => {
      const [x1, y1] = toSvg(apply(w, a))
      const [x2, y2] = toSvg(apply(w, b))
      lines.push(`M${x1.toFixed(0)},${y1.toFixed(0)}L${x2.toFixed(0)},${y2.toFixed(0)}`)
    }
    const [cx, cy] = toSvg(w.t)
    switch (pc.art) {
      case 'tube':
      case 'seatpost':
        seg([0, 0, 0], [p.length ?? 100, 0, 0])
        break
      case 'handlebar':
        seg([0, 0, -(p.length ?? 420) / 2], [0, 0, (p.length ?? 420) / 2])
        break
      case 'rim':
      case 'tyre':
        circles.push([cx, cy, p.outer ?? 320])
        break
      case 'hub':
        circles.push([cx, cy, 30])
        break
      case 'crankset':
        circles.push([cx, cy, model.g.bigRing])
        break
      case 'headsetBearing':
      case 'bbShell':
      case 'dropout':
        circles.push([cx, cy, 22])
        break
    }
  }
  return (
    <g className="ghost" fill="none" stroke={color} strokeOpacity={0.28} strokeWidth={4} strokeDasharray="16 12" strokeLinecap="round">
      {lines.length > 0 && <path d={lines.join('')} />}
      {circles.map(([x, y, r], k) => (
        <circle key={k} cx={x} cy={y} r={r} />
      ))}
    </g>
  )
}

export interface CaptionText {
  title: string
  figure: string
}

export const Mat = memo(function Mat({ model, caption }: { model: AtelierModel; caption: (n: ANode) => CaptionText }) {
  const m = model.mat
  const bay = model.bay
  const cells = model.nodes.filter((n) => n.role === 'make' && n.cell)
  const trays = model.nodes.filter((n) => n.role === 'phantom' && n.tray)
  const leaves = model.nodes.filter((n) => n.slot)
  const ticks: ReactNode[] = []
  for (let x = Math.ceil(m.x / 100) * 100; x <= m.x + m.w; x += 100) {
    const major = x % 500 === 0
    ticks.push(<line key={x} x1={x} x2={x} y1={m.y} y2={m.y + (major ? 46 : 22)} stroke="#bfe3ff" strokeOpacity={major ? 0.4 : 0.2} strokeWidth={major ? 4 : 2.4} />)
    if (major && x % 1000 === 0)
      ticks.push(
        <text key={`t${x}`} x={x + 10} y={m.y + 74} fontSize={30} fill="#bfe3ff" fillOpacity={0.4} fontFamily="JetBrains Mono Variable, monospace">
          {x / 1000} m
        </text>,
      )
  }
  return (
    <g className="mat">
      <defs>
        {cells.map((n) => (
          <clipPath key={n.id} id={`clip-${n.id}`}>
            <rect x={n.cell!.x} y={n.cell!.y} width={n.cell!.w} height={n.cell!.h} rx={30} />
          </clipPath>
        ))}
      </defs>
      <rect className="mat-bg" x={m.x - 400} y={m.y - 400} width={m.w + 800} height={m.h + 800} fill="#0b111b" />
      <rect className="mat-face" x={m.x} y={m.y} width={m.w} height={m.h} rx={40} fill="url(#mat-light)" />
      <rect className="mat-grid" x={m.x} y={m.y} width={m.w} height={m.h} rx={40} fill="url(#mat-grid)" />
      <rect className="mat-grid" x={m.x} y={m.y} width={m.w} height={m.h} rx={40} fill="url(#mat-grid-major)" />
      <rect x={m.x} y={m.y} width={m.w} height={m.h} rx={40} fill="none" stroke="#bfe3ff" strokeOpacity={0.18} strokeWidth={5} />
      <g className="ticks">{ticks}</g>

      <rect className="bay-light" x={bay.x} y={bay.y} width={bay.w} height={bay.h} rx={36} fill="url(#bay-light)" />
      <g className="bay-frame">
        <rect x={bay.x} y={bay.y} width={bay.w} height={bay.h} rx={36} fill="#ffffff" fillOpacity={0.02} stroke="#67e8f9" strokeOpacity={0.3} strokeWidth={4} strokeDasharray="26 18" />
        <CropMarks b={bay} color="#67e8f9" />
        <Ghost model={model} node={model.root} at={model.bayPose} color="#67e8f9" />
        <Header x={bay.x + 44} y={bay.y + 80} color="#f9a8d4" kind="FINAL ASSEMBLY" title={model.root.name} sub={`${model.root.line.item} · ${model.nodes.length} lines in the explosion`} />
      </g>

      {cells.map((n) => (
        <g key={n.id} className="cell" data-cell={n.id}>
          <rect x={n.cell!.x} y={n.cell!.y} width={n.cell!.w} height={n.cell!.h} rx={30} fill="#ffffff" fillOpacity={0.035} stroke={n.color} strokeOpacity={0.24} strokeWidth={3} />
          <rect x={n.area!.x} y={n.area!.y} width={n.area!.w} height={n.area!.h} rx={22} fill={n.color} fillOpacity={0.045} />
          <CropMarks b={n.area!} color={n.color} />
          {n.home && <Ghost model={model} node={n} at={n.home} color={n.color} />}
          <g clipPath={`url(#clip-${n.id})`}>
            <Header x={n.cell!.x + 40} y={n.cell!.y + 76} color={n.color} kind={n.find ? `FIND ${n.find} · MAKE` : 'MAKE'} title={n.name} sub={`${n.line.item} · ${fmt(n.line.usage)} ${n.line.unit.trim()} per parent`} />
          </g>
        </g>
      ))}

      {trays.map((n) => (
        <g key={n.id} className="tray" data-cell={n.id}>
          <rect x={n.tray!.x} y={n.tray!.y} width={n.tray!.w} height={n.tray!.h} rx={22} fill="#ffffff" fillOpacity={0.025} stroke="#cbd5e1" strokeOpacity={0.35} strokeWidth={3} strokeDasharray="16 12" />
          <text x={n.tray!.x + 26} y={n.tray!.y + 40} fontSize={22} fontWeight={700} letterSpacing={2} fill="#cbd5e1" fillOpacity={0.85}>
            PHANTOM KIT · {n.name.toUpperCase()}
          </text>
        </g>
      ))}

      <g className="captions">
        {leaves.map((n) => {
          const c = caption(n)
          const s = n.slot!
          return (
            <g key={n.id} className="cap" data-node={n.id} style={{ transform: `translate(${s.x}px, ${s.y + s.h - CAPTION + 14}px) scale(var(--cs, 1))` }}>
              <circle cx={6} cy={8} r={5} fill={n.color} />
              <text x={18} y={14} fontSize={19} fontWeight={600} fill={ink[200]}>
                {c.title}
              </text>
              <text x={18} y={38} fontSize={16} fill={ink[400]}>
                {c.figure}
              </text>
            </g>
          )
        })}
      </g>
    </g>
  )
})

function fmt(v: number) {
  return Number.isInteger(v) ? String(v) : v.toFixed(3).replace(/0+$/, '').replace(/\.$/, '')
}
