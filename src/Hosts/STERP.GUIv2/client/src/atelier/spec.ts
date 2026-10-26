import type { Tree, TreeNode } from '@/domain/tree'

// What the bicycle drawing reads from its bill lines: the descriptions of the parts the engine returned ("Rim 700C, 24 hole",
// "Crankset 172.5 mm, 50/34 chainrings", "Two pack paint, satin black"). Nothing here is a quantity or a cost; sizes the bill
// does not state fall back to a medium commuter.

export interface BikeSpec {
  /** The frame maker's name in the frame description ("Frame, gravel Trailhead, medium"). */
  brand: string
  /** The frame colour named by the paint line, and its finish. */
  paint: string
  paintName: string
  finish: 'satin' | 'gloss' | 'matt'
  /** Spoke holes per rim. */
  spokes: number
  chainLinks: number
  chainrings: number[]
  cogs: number[]
  crankMm: number
  rotorMm: number
  tyreMm: number
  barMm: number
  stemMm: number
  saddleMm: number
  seatpost: { diameter: number; length: number }
}

const WORDS: Record<string, number> = { six: 6, seven: 7, eight: 8, nine: 9, ten: 10, eleven: 11, twelve: 12, thirteen: 13 }

/** The first capture group of the pattern as a number, or the fallback. */
export function numberIn(description: string | undefined, pattern: RegExp, fallback: number): number {
  const m = description ? pattern.exec(description) : null
  const v = m ? Number(m[1]) : NaN
  return Number.isFinite(v) && v > 0 ? v : fallback
}

/** "Frame, gravel Trailhead, medium" -> "Trailhead": the capitalised word after the first comma. */
export function brandIn(description: string | undefined): string {
  for (const s of (description ?? '').split(',').slice(1)) {
    const word = s.trim().split(/\s+/).find((w) => /^[A-Z][a-z]{2,}/.test(w))
    if (word) return word
  }
  return 'STERP'
}

const PAINT_COLOURS: [RegExp, string, string][] = [
  [/black/, '#15171c', 'black'],
  [/white/, '#e9ecef', 'white'],
  [/silver|grey|gray/, '#9aa1aa', 'grey'],
  [/red/, '#b4141e', 'red'],
  [/orange/, '#e25a12', 'orange'],
  [/yellow/, '#e8b512', 'yellow'],
  [/green/, '#1f6b45', 'green'],
  [/blue/, '#1b4fa8', 'blue'],
  [/purple|violet/, '#5b2a9c', 'purple'],
]

export function paintIn(description: string | undefined): Pick<BikeSpec, 'paint' | 'paintName' | 'finish'> {
  const lower = (description ?? '').toLowerCase()
  const colour = PAINT_COLOURS.find(([re]) => re.test(lower))
  const finish = /matt/.test(lower) ? 'matt' : /gloss/.test(lower) ? 'gloss' : 'satin'
  return { paint: colour?.[1] ?? '#2a2d34', paintName: colour ? `${finish} ${colour[2]}` : finish, finish }
}

/** "Cassette 11-34, eleven speed" -> 11 cogs from 11 to 34 teeth, spaced the way cassettes are (geometric). */
export function cogsIn(description: string | undefined): number[] {
  const range = description ? /(\d{1,2})\s*-\s*(\d{2})/.exec(description) : null
  const lo = range ? Number(range[1]) : 11
  const hi = range ? Number(range[2]) : 34
  const lower = (description ?? '').toLowerCase()
  const word = Object.entries(WORDS).find(([w]) => new RegExp(`\\b${w}\\b`).test(lower))?.[1]
  const n = clamp(numberIn(description, /(\d+)\s*(?:speed|s\b)/i, word ?? 11), 2, 13)
  return [...Array(n).keys()].map((i) => Math.round(lo * Math.pow(hi / lo, i / (n - 1))))
}

const text = (n: TreeNode) => `${n.line.item} ${n.line.description}`.toUpperCase()

export function bikeSpecOf(tree: Tree): BikeSpec {
  const find = (re: RegExp) => tree.nodes.find((n) => n.depth > 0 && re.test(text(n)))?.line.description
  const frame = find(/^FG-FRAME|^FRAME[- ]|\bFRAME,/)
  const rim = find(/^RIM-|\bRIM\b/)
  const chain = find(/^CHAIN-|\bCHAIN\b(?!STAY)/)
  const crank = find(/CRANKSET/)
  const rings = crank ? /(\d{2})\s*\/\s*(\d{2})/.exec(crank) : null
  const paintLine = tree.nodes.find((n) => /PAINT/.test(text(n)) && PAINT_COLOURS.some(([re]) => re.test(n.line.description.toLowerCase())))
  const post = find(/SEATPOST/)
  const postSize = post ? /(\d{2}(?:\.\d)?)\s*x\s*(\d{3})/i.exec(post) : null
  return {
    brand: brandIn(frame),
    ...paintIn(paintLine?.line.description),
    spokes: clamp(Math.round(numberIn(rim, /(\d+)\s*hole/i, 24) / 2) * 2, 12, 40),
    chainLinks: clamp(numberIn(chain, /(\d+)\s*links?/i, 116), 90, 130),
    chainrings: rings ? [Number(rings[1]), Number(rings[2])].map((t) => clamp(t, 22, 60)) : [46, 32],
    cogs: cogsIn(find(/CASSETTE/)).map((t) => clamp(t, 9, 52)),
    crankMm: clamp(numberIn(crank, /(\d+(?:\.\d+)?)\s*mm/i, 172.5), 150, 185),
    rotorMm: clamp(numberIn(find(/BRAKE-DISC|ROTOR/), /(\d+)\s*mm/i, 160), 120, 220),
    tyreMm: clamp(numberIn(find(/^TYRE-|\bTYRE\b/), /x\s*(\d+)\s*c?/i, 32), 23, 50),
    barMm: clamp(numberIn(find(/HANDLEBAR/), /(\d+)\s*mm/i, 420), 360, 800),
    stemMm: clamp(numberIn(find(/^STEM-|\bSTEM\b/), /(\d+)\s*mm/i, 100), 40, 140),
    saddleMm: clamp(numberIn(find(/SADDLE/), /(\d+)\s*mm/i, 143), 120, 170),
    seatpost: { diameter: postSize ? clamp(Number(postSize[1]), 25, 34.9) : 27.2, length: postSize ? clamp(Number(postSize[2]), 250, 450) : 350 },
  }
}

export function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v))
}
