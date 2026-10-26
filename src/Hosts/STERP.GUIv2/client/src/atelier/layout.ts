import type { ANode, Box } from './types'

// The knolling layout: every made subassembly gets a cell on the mat (a build area where it comes together, its loose parts
// packed beside it in tidy rows, its own subassemblies' cells below), phantom kits get a tray inside their parent's cell, and
// the bike gets the bay in the middle with its loose parts underneath and the subassemblies' cells in columns either side.

export const CAPTION = 58
const PAD = 56
const HEAD = 120
const GAP = 70
const TRAY_PAD = 26
const TRAY_HEAD = 64

interface Measured {
  w: number
  h: number
  place: (x: number, y: number) => void
}

interface Packed {
  at: { x: number; y: number }[]
  w: number
  h: number
}

/** Shelf packing: left to right, a new row when the next one does not fit. */
export function shelf(sizes: { w: number; h: number }[], maxW: number, gapX: number, gapY: number): Packed {
  const at: { x: number; y: number }[] = []
  let x = 0
  let y = 0
  let rowH = 0
  let w = 0
  for (const s of sizes) {
    if (x > 0 && x + s.w > maxW) {
      y += rowH + gapY
      x = 0
      rowH = 0
    }
    at.push({ x, y })
    x += s.w + gapX
    rowH = Math.max(rowH, s.h)
    w = Math.max(w, x - gapX)
  }
  return { at, w, h: sizes.length ? y + rowH : 0 }
}

const isLeaf = (n: ANode) => n.role !== 'make' && n.role !== 'phantom' && n.role !== 'root'

/** The lines a phantom kit lays out: its leaves, and the leaves of phantoms inside it. */
export function phantomLeaves(n: ANode): ANode[] {
  return n.children.flatMap((c) => (c.role === 'phantom' ? phantomLeaves(c) : isLeaf(c) ? [c] : []))
}

function packItems(items: Measured[], cap: number): { packed: Packed; order: Measured[] } {
  const order = [...items].sort((a, b) => b.h - a.h)
  const area = order.reduce((s, m) => s + m.w * m.h, 0)
  const widest = order.reduce((s, m) => Math.max(s, m.w), 0)
  const maxW = Math.max(widest, Math.min(cap, Math.sqrt(area) * 1.35))
  return { packed: shelf(order, maxW, 44, 40), order }
}

export interface LayoutInput {
  /** size of the build area of a made node (its assembled drawing plus a margin) */
  areaSize: (n: ANode) => [number, number]
  /** footprint of a leaf line (with its caption) */
  slotSize: (n: ANode) => [number, number]
}

function slotItem(n: ANode, input: LayoutInput): Measured {
  const [w, h] = input.slotSize(n)
  return { w, h, place: (x, y) => void (n.slot = { x, y, w, h }) }
}

function trayItem(n: ANode, input: LayoutInput): Measured {
  const leaves = phantomLeaves(n)
  const items = leaves.map((l) => slotItem(l, input))
  const { packed, order } = packItems(items, 1400)
  const w = Math.max(320, packed.w + TRAY_PAD * 2)
  const h = TRAY_HEAD + packed.h + TRAY_PAD
  return {
    w,
    h,
    place: (x, y) => {
      n.tray = { x, y, w, h }
      order.forEach((m, i) => m.place(x + TRAY_PAD + packed.at[i].x, y + TRAY_HEAD + packed.at[i].y))
    },
  }
}

function looseOf(n: ANode, input: LayoutInput): Measured[] {
  return n.children.filter((c) => c.role !== 'make').map((c) => (c.role === 'phantom' ? trayItem(c, input) : slotItem(c, input)))
}

function cellItem(n: ANode, input: LayoutInput): Measured {
  const [aw, ah] = input.areaSize(n)
  const loose = looseOf(n, input)
  const subs = n.children.filter((c) => c.role === 'make').map((c) => cellItem(c, input))
  const { packed, order } = packItems(loose, 2400)
  const topW = Math.max(aw + (loose.length ? GAP + packed.w : 0), 140 + n.name.length * 19)
  const topH = Math.max(ah, packed.h)
  // the subassemblies' cells: the arrangement that keeps this cell closest to a landscape rectangle
  const widest = subs.reduce((s, m) => Math.max(s, m.w), 0)
  const all = subs.reduce((s, m) => s + m.w + GAP, 0)
  let subPacked = shelf(subs, Math.max(topW, widest), GAP, GAP)
  let best = Infinity
  for (const cap of [topW, topW * 1.5, topW * 2, all]) {
    const pk = shelf(subs, Math.max(cap, widest), GAP, GAP)
    const w = Math.max(topW, pk.w)
    const h = topH + (subs.length ? GAP + pk.h : 0)
    const score = Math.abs(Math.log(w / h / 1.4))
    if (score < best - 1e-6) {
      best = score
      subPacked = pk
    }
  }
  const w = PAD * 2 + Math.max(topW, subPacked.w)
  const h = HEAD + topH + (subs.length ? GAP + subPacked.h : 0) + PAD
  return {
    w,
    h,
    place: (x, y) => {
      n.cell = { x, y, w, h }
      n.area = { x: x + PAD, y: y + HEAD + (topH - ah) / 2, w: aw, h: ah }
      order.forEach((m, i) => m.place(x + PAD + aw + GAP + packed.at[i].x, y + HEAD + packed.at[i].y))
      subs.forEach((m, i) => m.place(x + PAD + subPacked.at[i].x, y + HEAD + topH + GAP + subPacked.at[i].y))
    },
  }
}

/** Lays the whole mat out; returns the mat and the bay (SVG coordinates, millimetres). */
export function layoutMat(root: ANode, input: LayoutInput): { mat: Box; bay: Box } {
  const [bw, bh] = input.areaSize(root)
  const loose = looseOf(root, input)
  const { packed, order } = packItems(loose, Math.max(bw, 2900))
  const subs = root.children.filter((c) => c.role === 'make').map((c) => ({ node: c, m: cellItem(c, input) }))
  // the subassemblies' cells in two columns either side of the bay, the tallest first into the shorter column
  const columns: { items: typeof subs; h: number; w: number }[] = [
    { items: [], h: 0, w: 0 },
    { items: [], h: 0, w: 0 },
  ]
  for (const s of [...subs].sort((a, b) => b.m.h - a.m.h)) {
    const col = columns[0].h <= columns[1].h ? columns[0] : columns[1]
    col.items.push(s)
    col.h += s.m.h + 140
    col.w = Math.max(col.w, s.m.w)
  }
  // keep the bill's order within each column
  for (const col of columns) col.items.sort((a, b) => subs.indexOf(a) - subs.indexOf(b))
  const centreW = Math.max(bw, packed.w)
  const top = 260
  const left = columns[0]
  const right = columns[1]
  const centreX = left.w + (left.items.length ? 220 : 0)
  const rightX = centreX + centreW + 220
  let y = top
  for (const s of left.items) {
    s.m.place(left.w - s.m.w, y)
    y += s.m.h + 140
  }
  y = top
  for (const s of right.items) {
    s.m.place(rightX, y)
    y += s.m.h + 140
  }
  const bay = { x: centreX + (centreW - bw) / 2, y: top, w: bw, h: bh }
  root.area = bay
  const itemsX = centreX + (centreW - packed.w) / 2
  order.forEach((m, i) => m.place(itemsX + packed.at[i].x, top + bh + 160 + packed.at[i].y))
  const centreH = bh + 160 + packed.h
  const h = top + Math.max(centreH, left.h - 140, right.h - 140) + 200
  const w = rightX + (right.items.length ? right.w : 0) + 60
  root.cell = { x: -60, y: 0, w: w + 60, h }
  return { mat: root.cell, bay }
}
