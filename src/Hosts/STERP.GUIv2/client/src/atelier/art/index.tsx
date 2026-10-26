import type { ReactNode } from 'react'
import type { Piece } from '../types'
import { BbShellArt, BeadArt, BearingArt, BoltArt, BracketArt, CylinderArt, DecalArt, DecalSheetArt, DropoutArt, HeadsetBearingArt, NutArt, SpoolArt, SwarfArt, TinArt, TubeArt, WasherArt } from './frameArt'
import { CassetteArt, HubArt, InnerTubeArt, NippleArt, RimArt, RotorArt, SpokeArt, TapeRingArt, TapeRollArt, TyreArt } from './wheelArt'
import { BellArt, CaliperArt, ChainArt, CoilArt, CranksetArt, DerailleurArt, GripArt, HandlebarArt, PedalArt, QuickLinkArt, RouteArt, SaddleArt, SeatpostArt, ShifterArt, StemArt } from './partArt'
import { CageArt, CartonArt, CartridgeArt, DabArt, FoamArt, KickstandArt, LabelArt, LampArt, ManualArt, MudguardArt, NoteArt, PartArt, RackArt, ReflectorArt, TicketArt } from './extraArt'

/** The drawing of one piece, by its art kind. */
export function Art({ piece }: { piece: Piece }): ReactNode {
  const p = piece.params ?? {}
  switch (piece.art) {
    case 'tube':
      return <TubeArt p={p} coat={piece.coat} />
    case 'bead':
      return <BeadArt p={p} />
    case 'spool':
      return <SpoolArt p={p} />
    case 'cylinder':
      return <CylinderArt p={p} />
    case 'swarf':
      return <SwarfArt />
    case 'tin':
      return <TinArt p={p} />
    case 'decalSheet':
      return <DecalSheetArt p={p} />
    case 'decal':
      return <DecalArt p={p} />
    case 'headsetBearing':
      return <HeadsetBearingArt p={p} />
    case 'bolt':
      return <BoltArt p={p} />
    case 'bbShell':
      return <BbShellArt p={p} />
    case 'bearing':
      return <BearingArt p={p} />
    case 'dropout':
      return <DropoutArt p={p} />
    case 'bracket':
      return <BracketArt />
    case 'washer':
      return <WasherArt p={p} />
    case 'nut':
      return <NutArt p={p} />
    case 'rim':
      return <RimArt p={p} />
    case 'hub':
      return <HubArt p={p} />
    case 'spoke':
      return <SpokeArt p={p} />
    case 'nipple':
      return <NippleArt />
    case 'tapeRoll':
      return <TapeRollArt p={p} />
    case 'tapeRing':
      return <TapeRingArt p={p} />
    case 'rotor':
      return <RotorArt p={p} />
    case 'cassette':
      return <CassetteArt p={p} />
    case 'tyre':
      return <TyreArt p={p} />
    case 'innerTube':
      return <InnerTubeArt p={p} />
    case 'handlebar':
      return <HandlebarArt p={p} />
    case 'stem':
      return <StemArt p={p} />
    case 'grip':
      return <GripArt p={p} />
    case 'shifter':
      return <ShifterArt p={p} />
    case 'bell':
      return <BellArt />
    case 'crankset':
      return <CranksetArt p={p} />
    case 'chain':
      return <ChainArt p={p} />
    case 'quickLink':
      return <QuickLinkArt />
    case 'derailleur':
      return <DerailleurArt p={p} />
    case 'caliper':
      return <CaliperArt />
    case 'coil':
      return <CoilArt p={p} />
    case 'route':
      return <RouteArt p={p} />
    case 'saddle':
      return <SaddleArt />
    case 'seatpost':
      return <SeatpostArt p={p} />
    case 'pedal':
      return <PedalArt p={p} />
    case 'mudguard':
      return <MudguardArt p={p} />
    case 'rack':
      return <RackArt p={p} />
    case 'lamp':
      return <LampArt p={p} />
    case 'kickstand':
      return <KickstandArt p={p} />
    case 'reflector':
      return <ReflectorArt p={p} />
    case 'cage':
      return <CageArt />
    case 'foam':
      return <FoamArt p={p} />
    case 'carton':
      return <CartonArt p={p} />
    case 'label':
      return <LabelArt p={p} />
    case 'manual':
      return <ManualArt p={p} />
    case 'cartridge':
      return <CartridgeArt p={p} />
    case 'dab':
      return <DabArt />
    case 'ticket':
      return <TicketArt p={p} />
    case 'note':
      return <NoteArt p={p} />
    case 'part':
      return <PartArt p={p} />
  }
}
