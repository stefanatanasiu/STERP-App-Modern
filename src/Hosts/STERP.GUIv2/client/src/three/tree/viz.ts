import type { NodeClass } from '@/domain/kinds'

/** What the 3D tree draws for one node; pages map API answers onto it (explosion occurrences, where-used parents). */
export interface VizNode {
  id: string
  depth: number
  parent: VizNode | null
  children: VizNode[]
  cls: NodeClass
  code: string
  title: string
  /** Short figure next to the code, already formatted (an API value). */
  figure?: string
  /** 0..1 visual weight (e.g. the API's extended cost relative to the largest line); undefined = size by class. */
  weight?: number
  /** Netting overlay: a shortage (API shortage > 0) or covered requirement. */
  ring?: 'shortage' | 'covered' | null
  /** The API flagged something on this line (warning text). */
  flagged?: boolean
}

export function flatten(root: VizNode): VizNode[] {
  const out: VizNode[] = []
  const walk = (n: VizNode) => {
    out.push(n)
    for (const c of n.children) walk(c)
  }
  walk(root)
  return out
}
