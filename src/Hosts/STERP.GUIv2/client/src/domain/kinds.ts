import type { StructureLine } from './tree'

// Visual vocabulary of an occurrence: what kind of thing it is (node type and supply from the explosion answer).

export type NodeClass = 'root' | 'made' | 'bought' | 'service' | 'phantom' | 'byproduct' | 'note'

export interface ClassMeta {
  label: string
  color: string
  hint: string
}

export const CLASS_META: Record<NodeClass, ClassMeta> = {
  root: { label: 'Product', color: '#f9a8d4', hint: 'The item being exploded' },
  made: { label: 'Made', color: '#a78bfa', hint: 'Manufactured: has its own bill and is exploded further' },
  bought: { label: 'Bought', color: '#22d3ee', hint: 'Purchased or stocked material: the explosion stops here' },
  service: { label: 'Service', color: '#34d399', hint: 'Service, labour or cost-only resource: no material moves' },
  phantom: { label: 'Phantom', color: '#cbd5e1', hint: 'Phantom grouping: its parts go straight into the parent' },
  byproduct: { label: 'By-product', color: '#fbbf24', hint: 'Recovered output of the parent process' },
  note: { label: 'Note', color: '#64748b', hint: 'Instruction or unconfigured option: no quantity' },
}

export function classify(line: StructureLine & { supply?: string }): NodeClass {
  if (line.level === 0) return 'root'
  const kind = line.kind.trim()
  const supply = (line.supply ?? '').trim()
  if (kind === 'PHANTOM') return 'phantom'
  if (kind === 'BYPROD') return 'byproduct'
  if (kind === 'NOTE') return 'note'
  if (kind === 'INTERMED') return 'made'
  if (supply === 'SERVICE' || supply === 'COSTONLY') return 'service'
  return 'bought'
}

export const STATUS_TONE: Record<string, string> = {
  RELEASED: 'emerald',
  DRAFT: 'amber',
  REVIEW: 'sky',
  SUPERSEDED: 'slate',
  RETIRED: 'zinc',
  ACTIVE: 'emerald',
  APPROVED: 'sky',
  COMPLETE: 'emerald',
  INCOMPLETE: 'amber',
  ERROR: 'rose',
  PASS: 'emerald',
  WARNING: 'amber',
  FAIL: 'rose',
  PENDING: 'sky',
  FAILED: 'rose',
  SKIPPED: 'slate',
  ADDED: 'emerald',
  REMOVED: 'rose',
  CHANGED: 'amber',
  RUNNING: 'sky',
  OK: 'emerald',
  ABENDED: 'rose',
  LOST: 'rose',
}

/** Human wording of the engine's stop reasons (BOMEXPL), shown next to the code. */
export const STOP_REASON_TEXT: Record<string, string> = {
  ROOT: 'Root of the explosion',
  MAKE: 'Made: exploded further',
  PHANTOM: 'Phantom: exploded into the parent',
  PURCHASED: 'Purchased: explosion stops',
  SERVICE: 'Service line',
  COSTONLY: 'Cost-only resource',
  NONSTOCK: 'Non-stock line',
  NOTE: 'Note line',
  RECOVERY: 'By-product recovery',
  USESTOCK: 'Use stock: not exploded',
  FROMSTOCK: 'Covered from stock: not exploded',
  DEPTHLIMIT: 'Depth limit reached',
  NOCHILDBOM: 'No usable bill for this made item',
  CYCLE: 'Cyclic structure',
  CONSOLIDATED: 'Consolidated into an earlier run',
  NOCHOICE: 'Alternate group without a selected choice',
  BADUNIT: 'Unit conversion missing',
}
