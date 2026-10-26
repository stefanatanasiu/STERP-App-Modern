import { envelope, json, list, query, seg, text, type CallOptions } from './http'
import type {
  BillByProduct,
  BillComponent,
  BillHeader,
  BillRevision,
  BulkPreview,
  BulkPreviewRequest,
  CostSet,
  CostedExplosion,
  DynamicRows,
  Explosion,
  ExplosionView,
  ItemMaster,
  JobRun,
  JobRunAccepted,
  NettedPlan,
  ReleaseOutcome,
  ReleaseRequest,
  RevisionDifference,
  RunAsUser,
  SessionContext,
  Validation,
  WhereUsedRow,
} from './types'

// Every function below is one existing operation of contracts/openapi (operationId in the comment). GUIv2 adds nothing to them.

export const platform = {
  /** platform_getSession */
  session: (options?: CallOptions) => json<SessionContext>('api', '/api/platform/v1/session', options),
  /** platform_listRunAsUsers */
  runAsUsers: (signal?: AbortSignal) => list<RunAsUser>('api', '/api/platform/v1/session/run-as-users', { signal }),
  /** platform_lookupMasterFile (BOM.HEADER, ITM.ITEM ...) */
  lookup: (maintCode: string, search: string, signal?: AbortSignal) =>
    json<DynamicRows>('api', `/api/platform/v1/lookups/master-files/${seg(maintCode)}${query({ search })}`, { signal }),
}

export interface ExplodeParams {
  revision: string
  quantity: number
  effectiveDate: string
  view: ExplosionView
  maxDepth: number
}

export interface CostParams {
  revision: string
  quantity: number
  effectiveDate: string
  costSet: string
}

export const bom = {
  /** bom_explodeBill */
  explode: (item: string, p: ExplodeParams, signal?: AbortSignal) =>
    envelope<Explosion>('api', `/api/bom/v1/items/${seg(item)}/explode`, { method: 'POST', body: p, signal }),
  /** bom_costBill */
  cost: (item: string, p: CostParams, signal?: AbortSignal) =>
    envelope<CostedExplosion>('api', `/api/bom/v1/items/${seg(item)}/cost`, { method: 'POST', body: p, signal }),
  /** bom_netBillRequirement */
  net: (item: string, p: { quantity: number; effectiveDate: string }, signal?: AbortSignal) =>
    envelope<NettedPlan>('api', `/api/bom/v1/items/${seg(item)}/net`, { method: 'POST', body: p, signal }),
  /** bom_getWhereUsed */
  whereUsed: (item: string, p: { asOf: string; maxLevels: number; includeDraft: boolean }, signal?: AbortSignal) =>
    list<WhereUsedRow>('api', `/api/bom/v1/items/${seg(item)}/where-used${query(p)}`, { signal }),
  /** bom_compareBillRevisions */
  compare: (bomRef: string, p: { fromRevision: string; toRevision: string; costSetCode: string }, signal?: AbortSignal) =>
    list<RevisionDifference>('api', `/api/bom/v1/bills/${seg(bomRef)}/revision-comparison${query(p)}`, { signal }),
  /** bom_listBillRevisions (a bill reference or a parent item is required by the procedure) */
  revisions: (p: { bomRef?: string; parentItemCode?: string; asOf: string }, signal?: AbortSignal) =>
    list<BillRevision>('api', `/api/bom/v1/bill-revisions${query(p)}`, { signal }),
  /** bom_getBillHeader (0 or 1 row) */
  header: async (bomRef: string, p: { revision?: string; asOf: string }, signal?: AbortSignal) =>
    (await list<BillHeader>('api', `/api/bom/v1/bills/${seg(bomRef)}/header${query(p)}`, { signal }))[0] ?? null,
  /** bom_listBillComponents */
  components: (bomRef: string, p: { revision?: string; asOf: string }, signal?: AbortSignal) =>
    list<BillComponent>('api', `/api/bom/v1/bills/${seg(bomRef)}/components${query(p)}`, { signal }),
  /** bom_listBillByProducts */
  byProducts: (bomRef: string, p: { revision?: string; asOf: string }, signal?: AbortSignal) =>
    list<BillByProduct>('api', `/api/bom/v1/bills/${seg(bomRef)}/by-products${query(p)}`, { signal }),
  /** bom_validateBill (CHECK: writes nothing) */
  validate: (bomRef: string, p: { bomRevision: string; effectiveDate: string }) =>
    envelope<Validation>('api', `/api/bom/v1/bills/${seg(bomRef)}/validate`, { method: 'POST', body: p }),
  /** bom_releaseBillRevision */
  release: (bomRef: string, revision: string, body: ReleaseRequest, idempotencyKey: string) =>
    list<ReleaseOutcome>('api', `/api/bom/v1/bills/${seg(bomRef)}/revisions/${seg(revision)}/release`, { method: 'POST', body, idempotencyKey }),
  /** bom_previewBulkChange (the preview never applies anything) */
  previewBulkChange: (changeRef: string, body: BulkPreviewRequest) =>
    envelope<BulkPreview>('api', `/api/bom/v1/engineering-changes/${seg(changeRef || ' ')}/preview`, { method: 'POST', body }),
  /** bom_submitBomrp010Run (STERP.Jobs) */
  submitBomrp010: (parameters: Record<string, string>, idempotencyKey: string) =>
    json<JobRunAccepted>('jobs', '/api/bom/v1/jobs/BOMRP010/runs', { method: 'POST', body: parameters, idempotencyKey }),
  /** bom_submitBomrp030Run (STERP.Jobs) */
  submitBomrp030: (parameters: Record<string, string>, idempotencyKey: string) =>
    json<JobRunAccepted>('jobs', '/api/bom/v1/jobs/BOMRP030/runs', { method: 'POST', body: parameters, idempotencyKey }),
}

export const cst = {
  /** cst_listCostSets */
  costSets: (signal?: AbortSignal) => list<CostSet>('api', '/api/cst/v1/cost-sets', { signal }),
}

export const itm = {
  /** itm_getItem */
  item: (itemCode: string, signal?: AbortSignal) => json<ItemMaster>('api', `/api/itm/v1/items/${seg(itemCode)}`, { signal }),
}

export const jobs = {
  /** jobs_getRun (STERP.Jobs) */
  run: (jobRunId: number, signal?: AbortSignal) => json<JobRun>('jobs', `/api/jobs/v1/runs/${jobRunId}`, { signal }),
  /** jobs_getRunSysout */
  sysout: (jobRunId: number, signal?: AbortSignal) => text('jobs', `/api/jobs/v1/runs/${jobRunId}/sysout`, { signal }),
  /** jobs_getRunSpool (core.JobRun.SpoolFile) */
  spool: (jobRunId: number, signal?: AbortSignal) => text('jobs', `/api/jobs/v1/runs/${jobRunId}/spool`, { signal }),
  /** jobs_getSpoolFile: a spool file by the path a run recorded (execution.spoolFiles) */
  spoolFile: (path: string, signal?: AbortSignal) => text('jobs', `/api/jobs/v1/spool-files/content${query({ path })}`, { signal }),
}
