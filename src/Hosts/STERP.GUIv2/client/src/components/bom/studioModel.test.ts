import { describe, expect, it } from 'vitest'
import type { Envelope, Explosion } from '@/api/types'
import { buildTree } from '@/domain/tree'
import { buildStudioModel, countIn, partOf, topperText } from './studioModel'
import cakeExplode from '@/test/fixtures/cake-explode.json'
import cakeLeaf from '@/test/fixtures/cake-leaf.json'

const tree = buildTree((cakeExplode as Envelope<Explosion>).data.lines)!
const leaf = (cakeLeaf as Envelope<Explosion>).data.lines
const model = buildStudioModel(tree, leaf, 'BAKERY')
const material = (item: string) => model.materials.find((m) => m.item === item)!

describe('buildStudioModel', () => {
  it('reads the cake from the bill lines the engine returned', () => {
    expect(model.bakery).toBe(true)
    expect(model.spec.layers).toBe(3)
    expect(model.spec.roses).toBe(5)
    expect(model.spec.candles).toBe(12)
    expect(model.spec.topperText).toBe('Happy Birthday')
    for (const p of ['sponge', 'buttercream', 'jam', 'fondant', 'roses', 'topper', 'candles', 'ribbon', 'drum', 'box', 'piping'] as const) {
      expect(model.spec.has[p]).toBe(true)
    }
  })

  it('links every purchased material to the parts it goes into', () => {
    expect(material('ING-FLOUR-SR').feeds).toEqual(['sponge'])
    expect(material('ING-SUGAR-ICING').feeds).toEqual(['buttercream', 'fondant'])
    expect(material('ING-BUTTER-UNSALTED').feeds).toEqual(['sponge', 'buttercream'])
    expect(material('ING-FLOWER-PASTE').feeds).toEqual(['roses'])
    expect(material('ING-JAM-RASPBERRY').feeds).toEqual(['jam'])
    expect(material('PKG-CAKE-BOX-10IN').feeds).toEqual(['box'])
  })

  it('keeps the engine leaf line unchanged and marks services and by-products', () => {
    expect(material('ING-SUGAR-ICING').leaf).toBe(leaf.find((l) => l.item === 'ING-SUGAR-ICING'))
    expect(material('SVC-DECORATING-HOUR').service).toBe(true)
    expect(material('SVC-DECORATING-HOUR').kind).toBe('hourglass')
    const crumb = material('ING-CAKE-CRUMB')
    expect(crumb.byproduct).toBe(true)
    expect(crumb.feeds).toEqual(['sponge'])
    expect(model.notes.map((n) => n.line.item)).toEqual(['NS-WORK-INSTRUCTION'])
  })

  it('is not a cake when the item master category is not BAKERY', () => {
    const generic = buildStudioModel(tree, leaf, 'FINISHED')
    expect(generic.bakery).toBe(false)
    expect(generic.kind).toBe('generic')
    expect(generic.materials.every((m) => m.feeds.join() === 'core')).toBe(true)
  })
})

describe('description readers', () => {
  it('reads counts and topper wording', () => {
    expect(countIn('Birthday candles, pack of twelve', 0)).toBe(12)
    expect(countIn('Candles, pack of 6', 0)).toBe(6)
    expect(countIn('Plain candles', 9)).toBe(9)
    expect(topperText('Cake topper, Happy Birthday, acrylic')).toBe('Happy Birthday')
    expect(partOf({ item: 'ING-CAKE-CRUMB', description: 'Sponge offcuts, recovered for cake pops' })).toBeNull()
  })
})
