import { useMemo } from 'react'
import { useCostedExplosion, useExplosion, useItem, type ReadingParams } from '@/api/hooks'
import { buildTree, overlayCosts } from '@/domain/tree'
import { fmtQty } from '@/domain/format'
import { useSession } from '@/state/session'
import { buildStudioModel, type StudioMaterial } from './studioModel'
import { StudioScene } from '@/three/studio/StudioScene'

/** The studio of one product, small and self-running: the overview's hero. */
export default function HeroStudio({ item }: { item: string }) {
  const businessDate = useSession((s) => s.session?.businessDate ?? '')
  const params: ReadingParams = { item, revision: '', quantity: 1, asOf: businessDate, costSet: 'BOMDEMO' }
  const explosion = useExplosion(params)
  const leaf = useExplosion(params, 'LEAF')
  const costed = useCostedExplosion(params)
  const master = useItem(item)
  const model = useMemo(() => {
    if (!explosion.data || !leaf.data || master.isPending) return null
    const tree = buildTree(explosion.data.data.lines)
    if (!tree) return null
    if (costed.data) overlayCosts(tree, costed.data.data.lines)
    return buildStudioModel(tree, leaf.data.data.lines, master.data?.category)
  }, [explosion.data, leaf.data, costed.data, master.data, master.isPending])
  if (!model) return <div className="size-full animate-pulse bg-[radial-gradient(ellipse_at_center,rgba(58,36,64,0.7),transparent_70%)]" />
  const figure = (m: StudioMaterial) => (m.leaf ? `${fmtQty(m.leaf.required, true)} ${m.leaf.unit}` : '')
  // a showcase: the card around it takes the click
  return (
    <div className="pointer-events-none size-full">
      <StudioScene
        model={model}
        view="assembled"
        showIngredients
        showBox={false}
        lighting="candle"
        autoRotate
        selection={null}
        onSelect={() => undefined}
        figure={figure}
        partFigure={() => ''}
        fitToken={0}
      />
    </div>
  )
}
