import { describe, expect, it } from 'vitest'
import type { Envelope, Explosion } from '@/api/types'
import { buildTree } from '@/domain/tree'
import bikeExplode from '@/test/fixtures/bike-explode.json'
import bikeLeaf from '@/test/fixtures/bike-leaf.json'
import { buildAtelier } from './model'
import { effectsAt, pieceAt, stepIndexAt, viewBox } from './evaluate'
import { compose, toSvg } from './projection'
import type { ANode } from './types'

const tree = buildTree((bikeExplode as Envelope<Explosion>).data.lines)!
const model = buildAtelier(tree)
const node = (item: string, parent?: string) => model.nodes.find((n) => n.line.item === item && (!parent || n.parent?.line.item === parent))!
const leaves = model.nodes.filter((n) => n.role !== 'root' && n.role !== 'make' && n.role !== 'phantom')
const close = (a: readonly number[], b: readonly number[]) => a.every((v, i) => Math.abs(v - b[i]) < 1e-6)

describe('buildAtelier', () => {
  it('stages every occurrence of the explosion, each leaf line as bike items it recognises', () => {
    expect(model.nodes).toHaveLength(95)
    for (const n of leaves) expect(n.pieces.length, n.line.item).toBeGreaterThan(0)
    const unknown = leaves.filter((n) => n.pieces.some((p) => p.art === 'part')).map((n) => n.line.item)
    expect(unknown).toEqual([])
    expect(model.spec).toMatchObject({ brand: 'Trailhead', paintName: 'satin black', spokes: 24, chainLinks: 116, chainrings: [50, 34], crankMm: 172.5, tyreMm: 32 })
  })

  it('draws the counts the bill states per parent', () => {
    const count = (n: ANode, art: string) => n.pieces.filter((p) => p.art === art).length
    expect(count(node('SPOKE-DB-294MM', 'SA-WHEEL-FRONT-700C'), 'spoke')).toBe(24)
    expect(count(node('NIPPLE-BRASS-12MM', 'SA-WHEEL-REAR-700C'), 'nipple')).toBe(24)
    expect(count(node('INSERT-M5-AL'), 'bolt')).toBe(6)
    expect(count(node('TYRE-700X32C'), 'tyre')).toBe(2)
    expect(count(node('TUBE-INNER-700C'), 'innerTube')).toBe(2)
    expect(count(node('PKG-FOAM-INSERT'), 'foam')).toBe(4)
    expect(count(node('BOLT-M5X16-SS', 'PH-HARDWARE-PACK-ROAD'), 'bolt')).toBe(4)
    expect(count(node('BEARING-6803-2RS'), 'bearing')).toBe(2)
    expect(count(node('BRAKE-CALIPER-FLAT'), 'caliper')).toBe(2)
  })

  it('cuts each tube stock into the tubes of the subassembly that uses it', () => {
    const keys = (item: string, parent: string) => node(item, parent).pieces.filter((p) => p.behavior === 'fly').map((p) => p.key)
    expect(keys('TUBE-AL7005-34.9X1.4', 'SA-FRAME-FRONT-TRI-M')).toEqual(['down'])
    expect(keys('TUBE-AL6061-31.8X1.6', 'SA-FRAME-FRONT-TRI-M')).toEqual(['top', 'seat', 'head'])
    expect(keys('TUBE-AL6061-28X1.5', 'SA-CHAINSTAY-PAIR')).toEqual(['chainstay-1', 'chainstay1'])
    expect(keys('TUBE-AL6061-28X1.5', 'SA-FORK-RIGID-ALU')).toEqual(['leg-1', 'leg1', 'steerer'])
    expect(node('SCRAP-AL-SWARF').pieces[0].behavior).toBe('emit')
  })

  it('builds bottom up: every subassembly before the one it goes into, the bike last', () => {
    const built = model.steps.filter((s) => s.id.startsWith('build-')).map((s) => s.node.line.item)
    const before = (a: string, b: string) => expect(built.indexOf(a), `${a} before ${b}`).toBeLessThan(built.indexOf(b))
    before('SA-HEADSET-ASSY', 'SA-FRAME-FRONT-TRI-M')
    before('SA-BB-ASSY-BSA', 'SA-FRAME-FRONT-TRI-M')
    before('SA-FRAME-FRONT-TRI-M', 'FG-FRAME-GRAVEL-M')
    before('SA-CHAINSTAY-PAIR', 'SA-FRAME-REAR-TRI-M')
    before('SA-WHEEL-FRONT-700C', 'FG-WHEELSET-700C-DISC')
    for (const s of model.steps) {
      expect(s.start).toBeGreaterThanOrEqual(0)
      expect(s.end).toBeLessThanOrEqual(1 + 1e-9)
    }
    // the bike's own lines are fitted after every subassembly is built
    const lastBuild = Math.max(...model.nodes.filter((n) => n.role === 'make').map((n) => n.build![1]))
    expect(model.root.build![0]).toBeGreaterThanOrEqual(lastBuild - 1e-9)
    for (const n of model.nodes) if (n.action && n.owner?.build) expect(n.action[0]).toBeGreaterThanOrEqual(n.owner.build[0] - 1e-9)
  })

  it('lays every loose line out on the mat without overlaps', () => {
    const slots = leaves.map((n) => n.slot!).filter(Boolean)
    expect(slots).toHaveLength(leaves.length)
    for (let i = 0; i < slots.length; i++)
      for (let j = i + 1; j < slots.length; j++) {
        const a = slots[i]
        const b = slots[j]
        const overlap = a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h
        expect(overlap, `${leaves[i].line.item} / ${leaves[j].line.item}`).toBe(false)
      }
  })

  it('starts fully decomposed and ends assembled in the bay', () => {
    for (const pc of model.pieces) {
      const s0 = pieceAt(model, pc, 0, 'build', true)
      if (pc.behavior === 'appear' || pc.behavior === 'emit') expect(s0.opacity, pc.id).toBe(0)
      else expect(close(s0.pose.t, pc.knolled.t), pc.id).toBe(true)
      if (pc.seat && pc.behavior === 'fly') {
        const s1 = pieceAt(model, pc, 1, 'build', true)
        expect(close(s1.pose.t, compose(model.bayPose, pc.seat).t), pc.id).toBe(true)
        expect(s1.opacity).toBe(1)
      }
    }
    expect(stepIndexAt(model, 0)).toBe(0)
    expect(stepIndexAt(model, 1)).toBe(model.steps.length)
  })

  it('paints the frame and fork over their window', () => {
    const top = node('TUBE-AL6061-31.8X1.6').pieces.find((p) => p.key === 'top')!
    const paint = model.effects.find((e) => e.variable === '--paint' && e.pieces.includes(top))!
    expect(effectsAt(model, paint.window[0] - 0.001, 'build').get(top)?.['--paint'] ?? 0).toBe(0)
    expect(effectsAt(model, 1, 'build').get(top)?.['--paint']).toBe(1)
    expect(effectsAt(model, 1, 'assembled').get(top)?.['--paint']).toBe(1)
  })

  it('frames the assembled bike inside the bay', () => {
    const box = viewBox(model, 'assembled', false)
    expect(box.w).toBeGreaterThan(1500)
    expect(box.w).toBeLessThan(2400)
    const [cx] = toSvg(model.bayPose.t)
    expect(cx).toBeGreaterThan(model.bay.x)
    expect(cx).toBeLessThan(model.bay.x + model.bay.w)
  })

  it('draws every material of the engine LEAF reading', () => {
    const drawn = new Set(model.nodes.filter((n) => n.pieces.length > 0).map((n) => n.line.item))
    for (const l of (bikeLeaf as Envelope<Explosion>).data.lines) expect(drawn.has(l.item), l.item).toBe(true)
  })

  it('still stages a line it does not recognise: a tagged tile that goes into its subassembly', () => {
    const lines = (bikeExplode as Envelope<Explosion>).data.lines.map((l) => (l.item === 'BELL-ALU' ? { ...l, item: 'WIDGET-X1', description: 'Widget, unknown kind' } : l))
    const other = buildAtelier(buildTree(lines)!)
    const widget = other.nodes.find((n) => n.line.item === 'WIDGET-X1')!
    expect(widget.pieces).toHaveLength(1)
    expect(widget.pieces[0].art).toBe('part')
    expect(widget.pieces[0].behavior).toBe('absorb')
    expect(widget.pieces[0].seat).toBeDefined()
    expect(pieceAt(other, widget.pieces[0], 1, 'build', false).opacity).toBe(0)
  })
})
