import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useContextKey } from '@/state/session'
import { bom, cst, itm, platform } from './endpoints'
import type { ExplosionView } from './types'

// Read hooks over the existing operations. The context key is part of every query key: switching company, site, business date
// or run-as user refetches everything. Engine-backed readings are POSTs but read-only (x-sterp-uow: Read-only).

const MINUTE = 60_000

export function useCostSets() {
  const ctx = useContextKey()
  return useQuery({ queryKey: ['cst', 'cost-sets', ctx], queryFn: ({ signal }) => cst.costSets(signal), staleTime: 10 * MINUTE })
}

export function useLookup(maintCode: string, search: string, enabled = true) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['lookup', maintCode, search, ctx],
    queryFn: ({ signal }) => platform.lookup(maintCode, search, signal),
    staleTime: 2 * MINUTE,
    placeholderData: keepPreviousData,
    enabled,
  })
}

export function useItem(itemCode: string | undefined) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['itm', 'item', itemCode, ctx],
    queryFn: ({ signal }) => itm.item(itemCode!, signal),
    enabled: !!itemCode,
    staleTime: 5 * MINUTE,
  })
}

export interface ReadingParams {
  item: string
  revision: string
  quantity: number
  asOf: string
  costSet: string
}

export function useExplosion(p: ReadingParams | null, view: ExplosionView = 'INDENTED') {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'explode', view, p, ctx],
    queryFn: ({ signal }) =>
      bom.explode(p!.item, { revision: p!.revision, quantity: p!.quantity, effectiveDate: p!.asOf, view, maxDepth: 0 }, signal),
    enabled: !!p?.item && p.quantity > 0,
    staleTime: MINUTE,
  })
}

export function useCostedExplosion(p: ReadingParams | null, enabled = true) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'cost', p, ctx],
    queryFn: ({ signal }) =>
      bom.cost(p!.item, { revision: p!.revision, quantity: p!.quantity, effectiveDate: p!.asOf, costSet: p!.costSet }, signal),
    enabled: enabled && !!p?.item && p.quantity > 0 && !!p.costSet,
    staleTime: MINUTE,
  })
}

export function useNettedPlan(p: ReadingParams | null, enabled = true) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'net', p?.item, p?.quantity, p?.asOf, ctx],
    queryFn: ({ signal }) => bom.net(p!.item, { quantity: p!.quantity, effectiveDate: p!.asOf }, signal),
    enabled: enabled && !!p?.item && p.quantity > 0,
    staleTime: MINUTE,
  })
}

export function useBillRevisions(p: { bomRef?: string; parentItemCode?: string; asOf: string } | null) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'revisions', p, ctx],
    queryFn: ({ signal }) => bom.revisions(p!, signal),
    enabled: !!p && !!(p.bomRef || p.parentItemCode),
    staleTime: MINUTE,
  })
}

export function useBillHeader(bomRef: string | undefined, revision: string | undefined, asOf: string) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'header', bomRef, revision, asOf, ctx],
    queryFn: ({ signal }) => bom.header(bomRef!, { revision, asOf }, signal),
    enabled: !!bomRef,
  })
}

export function useBillComponents(bomRef: string | undefined, revision: string | undefined, asOf: string) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'components', bomRef, revision, asOf, ctx],
    queryFn: ({ signal }) => bom.components(bomRef!, { revision, asOf }, signal),
    enabled: !!bomRef,
  })
}

export function useBillByProducts(bomRef: string | undefined, revision: string | undefined, asOf: string) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'by-products', bomRef, revision, asOf, ctx],
    queryFn: ({ signal }) => bom.byProducts(bomRef!, { revision, asOf }, signal),
    enabled: !!bomRef,
  })
}

export function useWhereUsed(item: string | undefined, asOf: string, maxLevels: number, includeDraft: boolean) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'where-used', item, asOf, maxLevels, includeDraft, ctx],
    queryFn: ({ signal }) => bom.whereUsed(item!, { asOf, maxLevels, includeDraft }, signal),
    enabled: !!item,
    staleTime: MINUTE,
  })
}

export function useRevisionComparison(bomRef: string | undefined, from: string, to: string, costSet: string) {
  const ctx = useContextKey()
  return useQuery({
    queryKey: ['bom', 'compare', bomRef, from, to, costSet, ctx],
    queryFn: ({ signal }) => bom.compare(bomRef!, { fromRevision: from, toRevision: to, costSetCode: costSet }, signal),
    enabled: !!bomRef && !!from && !!to,
  })
}
