import { describe, expect, it } from 'vitest'
import type { CostedExplosion, Envelope, Explosion, NettedPlan, WhereUsedRow } from '@/api/types'
import { buildTree, buildWhereUsedTree, childVerdict, displayOrder, overlayCosts, overlayNet, PATH_LIMIT } from './tree'
import cakeExplode from '@/test/fixtures/cake-explode.json'
import cakeCost from '@/test/fixtures/cake-cost.json'
import cakeNet from '@/test/fixtures/cake-net.json'
import bikeExplode from '@/test/fixtures/bike-explode.json'
import bikeCost from '@/test/fixtures/bike-cost.json'
import deepExplode from '@/test/fixtures/deep-explode.json'
import pinWhereUsed from '@/test/fixtures/pin-whereused.json'
import flourWhereUsed from '@/test/fixtures/flour-whereused.json'

// Fixtures: answers of the running STERP.Api on the demo dataset (bom_explodeBill INDENTED, bom_costBill BOMDEMO, bom_netBillRequirement).
const cake = (cakeExplode as Envelope<Explosion>).data
const bike = (bikeExplode as Envelope<Explosion>).data
const deep = (deepExplode as Envelope<Explosion>).data

const childItems = (tree: NonNullable<ReturnType<typeof buildTree>>, item: string) =>
  tree.nodes.find((n) => n.line.item === item)!.children.map((c) => c.line.item)

describe('buildTree', () => {
  it('rebuilds the celebration cake from its engine-ordered blocks', () => {
    const tree = buildTree(cake.lines)!
    expect(tree.nodes).toHaveLength(36)
    expect(tree.inferredCount).toBe(0)
    expect(tree.root.line.item).toBe('FG-CAKE-CELEB-8IN')
    expect(tree.root.children).toHaveLength(10)
    // SA-CAKE-BATTER-VAN follows SVC-DECORATING-HOUR in the answer, but belongs to the sponge layer
    expect(childItems(tree, 'SA-CAKE-SPONGE-8IN')).toEqual(['SA-CAKE-BATTER-VAN', 'PKG-PARCHMENT-8IN', 'ING-CAKE-CRUMB'])
    expect(childItems(tree, 'SA-CAKE-BATTER-VAN')).toHaveLength(7)
    expect(childItems(tree, 'SA-CAKE-BUTTERCREAM')).toEqual(['ING-BUTTER-UNSALTED', 'ING-SUGAR-ICING', 'ING-MILK-WHOLE', 'ING-VANILLA-EXTRACT'])
    expect(childItems(tree, 'SA-CAKE-FONDANT')).toEqual(['ING-SUGAR-ICING', 'ING-GLUCOSE-SYRUP', 'ING-GLYCERINE', 'ING-GELATINE'])
    expect(childItems(tree, 'PH-CAKE-DECORATION')).toEqual(['SA-CAKE-SUGARROSE', 'DECOR-RIBBON-15MM', 'DECOR-TOPPER-BIRTHDAY', 'DECOR-CANDLE-PACK'])
    expect(childItems(tree, 'SA-CAKE-SUGARROSE')).toEqual(['ING-FLOWER-PASTE', 'ING-FOOD-COLOUR-PINK', 'DECOR-FLORIST-WIRE'])
  })

  it('places every bicycle occurrence under a parent whose path it extends', () => {
    const tree = buildTree(bike.lines)!
    expect(tree.nodes).toHaveLength(bike.occurrences)
    for (const node of tree.nodes.slice(1)) {
      expect(node.parent).not.toBeNull()
      expect(node.depth).toBe(node.parent!.depth + 1)
      if (node.parent!.line.path.length < PATH_LIMIT) expect(node.line.path.startsWith(node.parent!.line.path + '/')).toBe(true)
    }
    expect(Math.max(...tree.nodes.map((n) => n.depth))).toBe(bike.depthReached)
  })

  it('keeps a twenty-level chain whose paths the API cut at 80 characters', () => {
    const tree = buildTree(deep.lines)!
    expect(tree.nodes).toHaveLength(21)
    for (let k = 0; k < 20; k++) {
      expect(tree.nodes[k].children.map((c) => c.line.item)).toEqual([tree.nodes[k + 1].line.item])
    }
    expect(displayOrder(tree.root).map((n) => n.depth)).toEqual([...Array(21).keys()])
  })

  it('tells a cut path apart from a complete one', () => {
    const parent = { level: 1, item: 'A', kind: 'INTERMED', path: 'X'.repeat(PATH_LIMIT) }
    expect(childVerdict(parent, { level: 2, item: 'B', kind: 'LEAF', path: 'X'.repeat(PATH_LIMIT) })).toBe('maybe')
    expect(childVerdict({ ...parent, path: 'ROOT/10:A' }, { level: 2, item: 'B', kind: 'LEAF', path: 'ROOT/10:A/10:B' })).toBe('yes')
    expect(childVerdict({ ...parent, path: 'ROOT/10:A' }, { level: 2, item: 'B', kind: 'LEAF', path: 'ROOT/20:C/10:B' })).toBe('no')
  })

  it('returns null for an empty answer', () => {
    expect(buildTree([])).toBeNull()
  })
})

describe('overlays', () => {
  it('pairs every costed line with its occurrence, values unchanged', () => {
    const tree = buildTree(cake.lines)!
    const costed = (cakeCost as Envelope<CostedExplosion>).data
    expect(overlayCosts(tree, costed.lines)).toBe(36)
    const flour = tree.nodes.find((n) => n.line.item === 'ING-FLOUR-SR')!
    expect(flour.cost).toBe(costed.lines.find((l) => l.item === 'ING-FLOUR-SR'))
    expect(tree.root.cost?.extendedCost).toBe(costed.lines[0].extendedCost)
  })

  it('pairs the bicycle cost lines', () => {
    const tree = buildTree(bike.lines)!
    const costed = (bikeCost as Envelope<CostedExplosion>).data
    expect(overlayCosts(tree, costed.lines)).toBe(costed.lines.length)
  })

  it('places netted lines in engine order, skipping the by-product the netting omits', () => {
    const tree = buildTree(cake.lines)!
    const netted = (cakeNet as Envelope<NettedPlan>).data
    const result = overlayNet(tree, netted.lines)
    expect(result).toEqual({ placed: netted.lines.length, complete: true })
    expect(tree.nodes.find((n) => n.line.item === 'ING-CAKE-CRUMB')!.net).toBeUndefined()
    const fondantIcing = tree.nodes.find((n) => n.line.item === 'ING-SUGAR-ICING' && n.parent!.line.item === 'SA-CAKE-FONDANT')!
    const icingLines = netted.lines.filter((l) => l.item === 'ING-SUGAR-ICING')
    expect(icingLines).toHaveLength(2)
    expect(fondantIcing.net).toBe(icingLines[1])
  })
})

describe('buildWhereUsedTree', () => {
  it('builds the upward chains of a component', () => {
    const rows = (flourWhereUsed as { items: WhereUsedRow[] }).items
    const { root, nodes } = buildWhereUsedTree('ING-FLOUR-SR', rows)
    expect(nodes.map((n) => n.item)).toEqual(['ING-FLOUR-SR', 'SA-CAKE-BATTER-VAN', 'SA-CAKE-SPONGE-8IN', 'FG-CAKE-CELEB-8IN'])
    expect(root.children[0].rows.map((r) => r.bomRef)).toEqual(['CK-BATTER-VAN'])
  })

  it('shares nodes between chains with a common start and keeps every bill that reaches a parent', () => {
    const rows = (pinWhereUsed as { items: WhereUsedRow[] }).items
    const { nodes } = buildWhereUsedTree('DEMO-PIN', rows)
    const directParents = new Set(rows.filter((r) => r.level === 1).map((r) => r.parentItemCode))
    expect(nodes.filter((n) => n.depth === 1).map((n) => n.item).sort()).toEqual([...directParents].sort())
    for (const row of rows) {
      const chain = row.pathText.split('<').map((s) => s.trim())
      expect(nodes.some((n) => n.rows.includes(row) && n.item === chain[chain.length - 1])).toBe(true)
    }
    // DEMO-OPT-TOP uses DEMO-PIN through two bills
    expect(nodes.find((n) => n.item === 'DEMO-OPT-TOP')!.rows.map((r) => r.bomRef).sort()).toEqual(['BOM-DEMO-BDY', 'BOM-DEMO-OPT'])
  })
})
