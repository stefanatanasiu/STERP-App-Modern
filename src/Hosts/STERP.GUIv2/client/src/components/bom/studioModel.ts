import type { CostedLine, ExplosionLine } from '@/api/types'
import { ancestors, descendants, type Tree, type TreeNode } from '@/domain/tree'

// Turns the API answers for one product (explosion INDENTED + LEAF, costed explosion) into what the studio draws: the parts of
// the product, the purchased materials around it and which part each material goes into. Quantities and costs are the API
// values; render details (sponge layers, roses, candles) are read from the bill lines the engine returned. The item master
// category picks the rendering: BAKERY is drawn as the cake, anything else abstractly. Bicycles (BICYCLE) have a studio of
// their own, the bike atelier (src/atelier).

export type ProductKind = 'cake' | 'bicycle' | 'generic'

export type CakePartKey = 'sponge' | 'jam' | 'buttercream' | 'fondant' | 'roses' | 'ribbon' | 'topper' | 'candles' | 'drum' | 'piping'
export type PartKey = CakePartKey | 'box' | 'core'

/** The cake's parts, bottom to top and inside out. */
export const PART_ORDER: PartKey[] = ['sponge', 'buttercream', 'jam', 'fondant', 'roses', 'topper', 'candles', 'ribbon', 'drum', 'box', 'piping', 'core']

export const PART_COLOR: Record<PartKey, string> = {
  sponge: '#f5c86b',
  buttercream: '#fff1cf',
  jam: '#ff4d7a',
  fondant: '#f8fafc',
  roses: '#f9a8d4',
  topper: '#facc15',
  candles: '#fdba74',
  ribbon: '#fde68a',
  drum: '#cbd5e1',
  box: '#e9d5ff',
  piping: '#a5f3fc',
  core: '#c4b5fd',
}

export type PropKind =
  | 'sack'
  | 'jar'
  | 'bowl'
  | 'butter'
  | 'eggs'
  | 'bottle'
  | 'vial'
  | 'tin'
  | 'sachet'
  | 'block'
  | 'tube'
  | 'wire'
  | 'discs'
  | 'cones'
  | 'spool'
  | 'candlebox'
  | 'board'
  | 'box'
  | 'crumbs'
  | 'hourglass'
  | 'parcel'

export interface StudioPart {
  key: PartKey
  node: TreeNode
  name: string
}

export interface StudioMaterial {
  item: string
  description: string
  name: string
  /** The engine's leaf requirement (bom_explodeBill LEAF): summed by the engine over every use. */
  leaf: ExplosionLine | null
  kind: PropKind
  tint: string
  /** The parts this material goes into, from its occurrences in the explosion. */
  feeds: PartKey[]
  occurrences: TreeNode[]
  byproduct: boolean
  service: boolean
}

export interface CakeSpec {
  layers: number
  roses: number
  candles: number
  topperText: string
  has: Record<PartKey, boolean>
}

export interface StudioModel {
  kind: 'cake' | 'generic'
  /** Kept for the cake studio: true when the product is drawn as a cake. */
  bakery: boolean
  root: TreeNode
  parts: Map<PartKey, StudioPart>
  materials: StudioMaterial[]
  spec: CakeSpec
  /** Instruction lines of the explosion (NOTE). */
  notes: TreeNode[]
}

export function productKindOf(category: string | null | undefined): ProductKind {
  const c = (category ?? '').trim().toUpperCase()
  if (c === 'BAKERY') return 'cake'
  if (c === 'BICYCLE') return 'bicycle'
  return 'generic'
}

export function partOrderOf(kind: ProductKind): PartKey[] {
  return kind === 'cake' ? PART_ORDER : ['core']
}

const GENERIC_LEAD = /^(subassembly|phantom|packaging|service|non-stock|kit)$/i

/** "Sponge layer, vanilla, 8 inch" -> "Sponge layer"; "Subassembly, rear triangle, medium" -> "Rear triangle". */
export const shortName = (description: string) => {
  const parts = description.split(',').map((s) => s.trim())
  let first = parts[0] ?? description
  if (GENERIC_LEAD.test(first) && parts[1]) first = parts[1].charAt(0).toUpperCase() + parts[1].slice(1)
  if (first.length <= 28) return first
  const cut = first.slice(0, 28)
  return `${cut.slice(0, Math.max(cut.lastIndexOf(' '), 16)).trim()}…`
}

/** The part of the cake a bill line is. */
export function partOf(line: Pick<ExplosionLine, 'item' | 'description'>): PartKey | null {
  const t = `${line.item} ${line.description}`.toUpperCase()
  if (/SPONGE/.test(t) && !/CRUMB|OFFCUT/.test(t)) return 'sponge'
  if (/BUTTERCREAM/.test(t)) return 'buttercream'
  if (/\bJAM\b|-JAM-/.test(t)) return 'jam'
  if (/FONDANT/.test(t)) return 'fondant'
  if (/ROSE/.test(t)) return 'roses'
  if (/TOPPER/.test(t)) return 'topper'
  if (/CANDLE/.test(t)) return 'candles'
  if (/RIBBON/.test(t)) return 'ribbon'
  if (/BOARD|DRUM/.test(t)) return 'drum'
  if (/CAKE-BOX|\bBOX\b/.test(t)) return 'box'
  if (/PIPING/.test(t)) return 'piping'
  return null
}

type Rule = [RegExp, PropKind, string]

const CAKE_RULES: Rule[] = [
  [/FLOUR/, 'sack', '#e7d3b0'],
  [/CASTER/, 'jar', '#e9c46a'],
  [/ICING/, 'bowl', '#ffffff'],
  [/BUTTER/, 'butter', '#f7dc6f'],
  [/EGG/, 'eggs', '#f3e2c7'],
  [/MILK/, 'bottle', '#ffffff'],
  [/VANILLA/, 'vial', '#7c4a1e'],
  [/BAKING|POWDER/, 'tin', '#ef4444'],
  [/JAM/, 'jar', '#9f1239'],
  [/GLUCOSE|SYRUP/, 'jar', '#f5b041'],
  [/GLYCERINE/, 'vial', '#dbeafe'],
  [/GELATINE/, 'sachet', '#fde68a'],
  [/FLOWER|PASTE/, 'block', '#fafafa'],
  [/COLOUR|COLOR/, 'tube', '#ec4899'],
  [/WIRE/, 'wire', '#16a34a'],
  [/PARCHMENT/, 'discs', '#f5ecd7'],
  [/PIPING/, 'cones', '#e0f2fe'],
  [/RIBBON/, 'spool', '#f3ead6'],
  [/CANDLE/, 'candlebox', '#f472b6'],
  [/BOARD|DRUM/, 'board', '#d1d5db'],
  [/BOX/, 'box', '#f5f5f4'],
  [/CRUMB|OFFCUT/, 'crumbs', '#e0b56a'],
]

export function propKindOf(item: string, description: string, unit: string, service: boolean): { kind: PropKind; tint: string } {
  if (service) return { kind: 'hourglass', tint: '#34d399' }
  const t = `${item} ${description}`.toUpperCase()
  for (const [re, k, tint] of CAKE_RULES) if (re.test(t)) return { kind: k, tint }
  const u = unit.toUpperCase()
  if (u === 'KG' || u === 'G') return { kind: 'sack', tint: '#d6c4a4' }
  if (u === 'L' || u === 'ML') return { kind: 'bottle', tint: '#bae6fd' }
  if (u === 'M') return { kind: 'spool', tint: '#e5e7eb' }
  return { kind: 'parcel', tint: '#c4b5fd' }
}

const NUMBER_WORDS: Record<string, number> = { one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10, eleven: 11, twelve: 12, sixteen: 16, twenty: 20, dozen: 12 }

/** "Birthday candles, pack of twelve" -> 12; "pack of 6" -> 6. */
export function countIn(description: string, fallback: number): number {
  const lower = description.toLowerCase()
  const digits = /pack of (\d+)|(\d+)\s*(?:per pack|candles)/.exec(lower)
  if (digits) return Number(digits[1] ?? digits[2])
  for (const [word, n] of Object.entries(NUMBER_WORDS)) if (new RegExp(`\\b${word}\\b`).test(lower)) return n
  return fallback
}

/** "Cake topper, Happy Birthday, acrylic" -> "Happy Birthday". */
export function topperText(description: string): string {
  const parts = description.split(',').map((s) => s.trim()).filter(Boolean)
  const middle = parts.find((p, i) => i > 0 && !/acrylic|wood|card|topper/i.test(p))
  return middle || 'Celebrate'
}

/** The part a material occurrence goes into: the first part on its path below the product. */
function partForOccurrence(node: TreeNode): PartKey | null {
  const own = partOf(node.line)
  const chain = [node, ...ancestors(node)].filter((n) => n.depth > 0).reverse()
  for (const n of chain) {
    const p = partOf(n.line)
    if (p) return p
  }
  return own
}

export function buildStudioModel(tree: Tree, leafLines: ExplosionLine[], category: string | null | undefined): StudioModel {
  const root = tree.root
  const kind = productKindOf(category) === 'cake' ? 'cake' : 'generic'
  const bakery = kind === 'cake'
  const parts = new Map<PartKey, StudioPart>()
  if (bakery) {
    for (const n of tree.nodes) {
      if (n.depth === 0 || n.depth > 2) continue
      const p = partOf(n.line)
      if (p && !parts.has(p) && !(n.line.kind.trim() === 'BYPROD')) parts.set(p, { key: p, node: n, name: shortName(n.line.description) })
    }
  }

  const byItem = new Map<string, TreeNode[]>()
  for (const n of tree.nodes) {
    if (n.depth === 0) continue
    const list = byItem.get(n.line.item) ?? []
    list.push(n)
    byItem.set(n.line.item, list)
  }

  const feedsOf = (occurrences: TreeNode[]): PartKey[] => {
    if (!bakery) return ['core']
    return unique(occurrences.map(partForOccurrence).filter((p): p is PartKey => !!p))
  }

  const materials: StudioMaterial[] = []
  const seen = new Set<string>()
  // purchased materials and services: the engine's LEAF reading, in its order
  for (const leaf of leafLines) {
    if (seen.has(leaf.item)) continue
    seen.add(leaf.item)
    const occurrences = byItem.get(leaf.item) ?? []
    const service = leaf.supply.trim() === 'SERVICE' || leaf.supply.trim() === 'COSTONLY'
    const { kind: prop, tint } = propKindOf(leaf.item, leaf.description, leaf.unit, service)
    materials.push({ item: leaf.item, description: leaf.description, name: shortName(leaf.description), leaf, kind: prop, tint, feeds: feedsOf(occurrences), occurrences, byproduct: false, service })
  }
  // by-products come out of a part rather than going into one
  for (const n of tree.nodes) {
    if (n.line.kind.trim() !== 'BYPROD' || seen.has(n.line.item)) continue
    seen.add(n.line.item)
    const occurrences = byItem.get(n.line.item) ?? []
    const { kind: prop, tint } = propKindOf(n.line.item, n.line.description, n.line.unit, false)
    const from = n.parent && bakery ? partForOccurrence(n.parent) : null
    materials.push({
      item: n.line.item,
      description: n.line.description,
      name: shortName(n.line.description),
      leaf: null,
      kind: prop,
      tint,
      feeds: from ? [from] : ['core'],
      occurrences,
      byproduct: true,
      service: false,
    })
  }

  const order = partOrderOf(kind)
  const usage = (key: PartKey, fallback: number) => {
    const n = parts.get(key)?.node
    return n ? Math.round(n.line.usage) : fallback
  }
  const candleNode = parts.get('candles')?.node
  const topperNode = parts.get('topper')?.node
  const has = Object.fromEntries(order.map((k) => [k, parts.has(k)])) as Record<PartKey, boolean>
  const spec: CakeSpec = {
    layers: clamp(usage('sponge', 3), 1, 6),
    roses: clamp(usage('roses', 0), 0, 12),
    candles: candleNode ? clamp(countIn(candleNode.line.description, 12), 0, 16) : 0,
    topperText: topperNode ? topperText(topperNode.line.description) : '',
    has,
  }

  const rank = (m: StudioMaterial) => {
    const i = order.indexOf(m.feeds[0] ?? 'core')
    return (m.byproduct ? 100 : 0) + (m.service ? 50 : 0) + (i < 0 ? 40 : i)
  }
  materials.sort((a, b) => rank(a) - rank(b))

  return { kind, bakery, root, parts, materials, spec, notes: tree.nodes.filter((n) => n.line.kind.trim() === 'NOTE') }
}

/** The parts drawn inside a part, from the explosion structure. */
export function subPartsOf(model: StudioModel, part: PartKey): PartKey[] {
  const p = model.parts.get(part)
  if (!p) return []
  const inside = new Set(descendants(p.node).map((n) => n.id))
  return [...model.parts.values()].filter((q) => q.key !== part && inside.has(q.node.id)).map((q) => q.key)
}

/** The costed lines of every occurrence of an item (bom_costBill), unchanged. */
export function costsOf(material: StudioMaterial): CostedLine[] {
  return material.occurrences.map((o) => o.cost).filter((c): c is CostedLine => !!c)
}

function unique<T>(values: T[]): T[] {
  return [...new Set(values)]
}

function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v))
}
