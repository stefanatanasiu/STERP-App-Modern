import type * as THREE from 'three'
import type { PartKey } from '@/components/bom/studioModel'

/** How the product is shown: assembled, opened up (the cake's cutaway) or exploded into its parts. */
export type ProductView = 'assembled' | 'cutaway' | 'exploded'

/** Studio lighting; 'candle' is the cake's candlelight. */
export type Lighting = 'studio' | 'candle'

/** World positions where the material flows land, one per part, kept up to date by the product model every frame. */
export type Anchors = Partial<Record<PartKey, THREE.Vector3>>
