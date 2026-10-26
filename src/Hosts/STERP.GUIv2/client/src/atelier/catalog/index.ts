import { bbShell, bearings, bracket, decals, dropout, fasteners, fillerWire, gasCylinder, headset, paintTin, stock, swarf } from './frame'
import { cassette, hub, nipples, rim, rimTape, rotor, spokes, tyres } from './wheels'
import { bell, cableRun, calipers, chain, crankset, derailleur, ferrules, grips, handlebar, pedals, quickLink, saddle, seatpost, shifter, stem } from './parts'
import { bottleCage, carton, foam, grease, kickstand, lamps, manual, mudguards, note, part, rack, reflectors, shippingLabel, ticket } from './extras'
import { text, type Ctx, type ItemRecipe } from './types'

export * from './types'
export { COCKPIT_LYING } from './parts'
export { cartonBox, serviceIcon } from './extras'
export { jointsOf, membersOf } from './frame'

/** How an occurrence takes part in the build. */
export type Role = 'root' | 'make' | 'phantom' | 'part' | 'stock' | 'supply' | 'service' | 'note' | 'byproduct'

export function roleOf(line: Ctx['line']): Role {
  if (line.level === 0) return 'root'
  const kind = line.kind.trim()
  const supply = line.supply.trim()
  if (kind === 'PHANTOM') return 'phantom'
  if (kind === 'NOTE') return 'note'
  if (kind === 'BYPROD') return 'byproduct'
  if (kind === 'INTERMED') return 'make'
  if (supply === 'SERVICE' || supply === 'COSTONLY') return 'service'
  const t = text(line)
  if (/^TUBE-AL|ALUMINIUM TUBE|ALUMINUM TUBE|STEEL TUBE/.test(t)) return 'stock'
  if (/WIRE-TIG|FILLER WIRE|ARGON|^GAS-|PRIMER|PAINT|GREASE|RIMTAPE|RIM TAPE|HOSE|HOUSING|CABLE-|INNER CABLE/.test(t) && !/FERRULE/.test(t)) return 'supply'
  return 'part'
}

/** The pieces drawn for one bill line (a leaf of the explosion: made and phantom lines are drawn by their children). */
export function recipeFor(c: Ctx, name: string): ItemRecipe {
  const t = text(c.line)
  const kind = c.line.kind.trim()
  const supply = c.line.supply.trim()
  if (kind === 'NOTE') return note(c)
  if (kind === 'BYPROD') return /SWARF|SCRAP|CHIP/.test(t) ? swarf() : part(c, name, 'emit')
  if (supply === 'SERVICE' || supply === 'COSTONLY') return ticket(c, name)
  // packaging first: "moulded foam insert" is not a threaded insert
  if (/PKG-BOX|CARTON/.test(t)) return carton(c)
  if (/FOAM/.test(t)) return foam(c)
  if (/LABEL/.test(t)) return shippingLabel(c)
  if (/MANUAL/.test(t)) return manual(c)
  if (/INNER TUBE|TUBE-INNER/.test(t)) return tyres(c, true)
  if (/^TYRE-|\bTYRE\b|\bTIRE\b/.test(t)) return tyres(c, false)
  if (/^TUBE-AL|ALUMINIUM TUBE|ALUMINUM TUBE|STEEL TUBE/.test(t)) return stock(c)
  if (/WIRE-TIG|FILLER WIRE/.test(t)) return fillerWire(c)
  if (/ARGON|^GAS-|GAS CYLINDER/.test(t)) return gasCylinder(c)
  if (/PRIMER/.test(t)) return paintTin(c, true, '#8b9099')
  if (/PAINT/.test(t)) return paintTin(c, false, c.spec.paint)
  if (/DECAL/.test(t)) return decals(c)
  if (/RIMTAPE|RIM TAPE/.test(t)) return rimTape(c)
  if (/HEADSET/.test(t)) return headset(c)
  if (/BB-SHELL|BOTTOM BRACKET SHELL/.test(t)) return bbShell(c)
  if (/BEARING/.test(t)) return bearings(c)
  if (/DROPOUT/.test(t)) return dropout(c)
  if (/BRKT|BRACKET/.test(t)) return bracket(c)
  if (/INSERT|RIVNUT/.test(t)) return fasteners(c, 'insert')
  if (/^RIM-|\bRIM\b/.test(t)) return rim(c)
  if (/^HUB-|\bHUB\b/.test(t)) return hub(c)
  if (/NIPPLE/.test(t)) return nipples(c)
  if (/SPOKE/.test(t)) return spokes(c)
  if (/BRAKE-DISC|ROTOR/.test(t)) return rotor(c)
  if (/CASSETTE/.test(t)) return cassette(c)
  if (/HANDLEBAR/.test(t)) return handlebar(c)
  if (/^STEM-|\bSTEM\b/.test(t)) return stem(c)
  if (/GRIP/.test(t)) return grips(c)
  if (/SHIFTER/.test(t)) return shifter(c)
  if (/CRANKSET|CHAINSET/.test(t)) return crankset(c)
  if (/LINK-QUICK|QUICK LINK/.test(t)) return quickLink(c)
  if (/^CHAIN-|\bCHAIN\b(?!STAY|RING)/.test(t)) return chain(c)
  if (/DERAILLEUR/.test(t)) return derailleur(c)
  if (/CALIPER/.test(t)) return calipers(c)
  if (/FERRULE/.test(t)) return ferrules(c)
  if (/HOSE|HOUSING|CABLE/.test(t)) return cableRun(c)
  if (/SADDLE/.test(t)) return saddle(c)
  if (/SEATPOST/.test(t)) return seatpost(c)
  if (/PEDAL/.test(t)) return pedals(c)
  if (/MUDGUARD|FENDER/.test(t)) return mudguards(c)
  if (/\bRACK\b|^RACK-/.test(t)) return rack(c)
  if (/REFLECTOR/.test(t)) return reflectors(c)
  if (/\bLIGHT|LAMP/.test(t)) return lamps(c)
  if (/KICKSTAND/.test(t)) return kickstand(c)
  if (/\bBELL\b|^BELL-/.test(t)) return bell(c)
  if (/BOTTLE/.test(t)) return bottleCage(c)
  if (/SCREW/.test(t)) return fasteners(c, 'screw')
  if (/BOLT/.test(t)) return fasteners(c, 'bolt')
  if (/\bNUT\b|^NUT-/.test(t)) return fasteners(c, 'nut')
  if (/WASHER/.test(t)) return fasteners(c, 'washer')
  if (/GREASE|LUBE/.test(t)) return grease(c)
  return part(c, name)
}
