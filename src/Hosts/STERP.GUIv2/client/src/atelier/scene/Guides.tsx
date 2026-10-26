import { memo, useMemo } from 'react'
import { pieceAt } from '../evaluate'
import { toSvg } from '../projection'
import { actionsOf } from '../timeline'
import type { ANode, AtelierModel } from '../types'

// The exploded view's drawing conventions: a dashed leader from each line's exploded position back to where it sits, and a
// balloon with its find number in the bill (phantom kit parts as kit.part, the way the bill nests them). Balloons are relaxed
// apart so they never sit on each other.

function under(n: ANode): ANode[] {
  return [n, ...n.children.flatMap(under)]
}

const SPACING = 104

export const Guides = memo(function Guides({ model, packaging }: { model: AtelierModel; packaging: boolean }) {
  const items = useMemo(() => {
    const out: { n: ANode; a: [number, number]; b: [number, number]; c: [number, number]; label: string }[] = []
    for (const n of actionsOf(model.root)) {
      let ax = 0
      let ay = 0
      let bx = 0
      let by = 0
      let k = 0
      for (const pc of under(n).flatMap((x) => x.pieces)) {
        const e = pieceAt(model, pc, 1, 'exploded', packaging)
        if (e.opacity <= 0) continue
        const s = pieceAt(model, pc, 1, 'assembled', packaging)
        const [x1, y1] = toSvg(s.pose.t)
        const [x2, y2] = toSvg(e.pose.t)
        ax += x1
        ay += y1
        bx += x2
        by += y2
        k++
      }
      if (!k) continue
      const label = n.parent && n.parent.role === 'phantom' ? `${n.parent.find}.${n.find}` : n.find
      const b: [number, number] = [bx / k, by / k]
      out.push({ n, a: [ax / k, ay / k], b, c: [b[0], b[1] - 60], label })
    }
    // relax the balloons apart, each held loosely to its own spot
    for (let it = 0; it < 160; it++) {
      for (let i = 0; i < out.length; i++)
        for (let j = i + 1; j < out.length; j++) {
          const p = out[i].c
          const q = out[j].c
          let dx = q[0] - p[0]
          let dy = q[1] - p[1]
          let d = Math.hypot(dx, dy)
          if (d >= SPACING) continue
          if (d < 1e-3) {
            dx = Math.cos(i + j)
            dy = Math.sin(i + j)
            d = 1
          }
          const push = (SPACING - d) / 2
          p[0] -= (dx / d) * push
          p[1] -= (dy / d) * push
          q[0] += (dx / d) * push
          q[1] += (dy / d) * push
        }
      for (const o of out) {
        o.c[0] += (o.b[0] - o.c[0]) * 0.012
        o.c[1] += (o.b[1] - 60 - o.c[1]) * 0.012
      }
    }
    return out
  }, [model, packaging])
  return (
    <g className="guides">
      {items.map(({ n, a, b }) =>
        Math.hypot(b[0] - a[0], b[1] - a[1]) > 24 ? (
          <g key={`l${n.id}`}>
            <line x1={a[0]} y1={a[1]} x2={b[0]} y2={b[1]} stroke="#cbd5e1" strokeOpacity={0.42} strokeWidth={2.6} strokeDasharray="12 9" />
            <circle cx={a[0]} cy={a[1]} r={5} fill={n.color} opacity={0.8} />
          </g>
        ) : null,
      )}
      {items.map(({ n, b, c }) => (
        <line key={`t${n.id}`} x1={b[0]} y1={b[1]} x2={c[0]} y2={c[1]} stroke={n.color} strokeOpacity={0.55} strokeWidth={2} />
      ))}
      {items.map(({ n, c, label }) => (
        <g key={`b${n.id}`} className="balloon" data-node={n.id} style={{ transform: `translate(${c[0]}px, ${c[1]}px) scale(var(--hs, 1))` }}>
          <circle r={label.length > 3 ? 30 : 23} fill="#0b0f1a" stroke={n.color} strokeWidth={3} />
          <text textAnchor="middle" y={label.length > 3 ? 5 : 7} fontSize={label.length > 3 ? 14 : 18} fontWeight={700} fill="#f5f7fc">
            {label}
          </text>
        </g>
      ))}
    </g>
  )
})
