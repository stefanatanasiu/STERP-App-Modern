// Shapes of the existing STERP API operations GUIv2 calls (contracts/openapi/STERP-*.openapi.yaml). Read-only mirrors of the
// binding schemas: GUIv2 displays these values and never recalculates them.

export interface Outcome {
  returnCode: number
  severity: string
  reasonCode: string
  message: string
  messageCount: number
  documentRef?: string | null
  correlationId: string
}

export interface Envelope<T> {
  outcome: Outcome
  data: T
}

// ---------------------------------------------------------------- platform

export interface AccessEntry {
  companyCode: string
  companyName: string
  siteCode: string
  siteName: string
  nation: string
  calendarCode: string
  baseCurrency: string
}

export interface SessionUser {
  userCode: string
  displayName: string
  windowsLogin: string
  emailAddress: string | null
  jobTitle: string | null
  defaultCompanyCode: string | null
  defaultSiteCode: string | null
  isReadOnly: boolean
  isActive: boolean
  businessDate: string
}

export interface SessionContext {
  windowsLogin: string
  user: SessionUser
  realUserCode: string
  isSysAdm: boolean
  isImpersonating: boolean
  title: string
  access: AccessEntry[]
  selected: AccessEntry | null
  businessDate: string
  fiscalPeriod: { fiscalYear: number; periodNo: number; status: string; periodText: string } | null
  message: string
}

export interface RunAsUser {
  userCode: string
  displayName: string
}

export interface DynamicRows {
  columns: { name: string; sqlType: string }[]
  items: Record<string, unknown>[]
  count: number
}

// ---------------------------------------------------------------- BOM: engine-backed readings

export type ExplosionView = 'INDENTED' | 'SINGLE' | 'LEAF'

export interface ExplosionLine {
  level: number
  item: string
  revision: string
  description: string
  kind: string
  supply: string
  basis: string
  usage: number
  unit: string
  required: number
  unrounded: number
  stopReason: string
  path: string
  warning: string
}

export interface Explosion {
  completeness: string
  stopSummary: string
  occurrences: number
  depthReached: number
  lines: ExplosionLine[]
}

export interface CostedLine {
  level: number
  item: string
  revision: string
  description: string
  kind: string
  element: string
  source: string
  required: number
  unit: string
  unitCost: number
  extendedCost: number
  branchCost: number
  path: string
  warning: string
}

export interface CostedExplosion {
  completeness: string
  occurrences: number
  missingRates: number
  totalCost: number
  unitCost: number
  materialCost: number
  conversionCost: number
  recoveryCredit: number
  errorPath: string
  lines: CostedLine[]
}

export interface NettedLine {
  level: number
  item: string
  description: string
  unit: string
  required: number
  stockUsed: number
  inbound: number
  toMake: number
  shortage: number
  excess: number
  stopReason: string
}

export interface NettedPlan {
  maxBuildable: number
  shortages: number
  completeness: string
  lines: NettedLine[]
}

// ---------------------------------------------------------------- BOM: structure reads

export interface BillRevision {
  bomHeaderId: number
  companyCode: string
  siteCode: string | null
  bomRef: string
  bomRevision: string
  parentItemCode: string
  purpose: string
  variantCode: string | null
  jobRef: string | null
  description: string | null
  status: string
  nominalBasis: number
  yieldFactor: number
  productionQuantum: number
  batchSize: number | null
  isPhantom: boolean
  effectiveFrom: string | null
  effectiveTo: string | null
  authorUser: string | null
  reviewerUser: string | null
  releasedBy: string | null
  releasedAt: string | null
  changeRef: string | null
  componentCount: number
  isEffectiveNow: boolean
  selectionRank: number | null
}

export interface BillHeader {
  bomHeaderId: number
  companyCode: string
  siteCode: string | null
  bomRef: string
  bomRevision: string
  parentItemCode: string
  parentRevision: string | null
  purpose: string
  variantCode: string | null
  jobRef: string | null
  description: string | null
  nominalBasis: number
  yieldFactor: number
  productionQuantum: number
  batchSize: number | null
  costingLotSize: number | null
  isPhantom: boolean
  effectiveFrom: string | null
  effectiveTo: string | null
  status: string
  authorUser: string | null
  reviewerUser: string | null
  releasedBy: string | null
  releasedAt: string | null
  changeRef: string | null
  changeReason: string | null
  engineeringNotes: string | null
  parentDescription: string | null
  stockUom: string | null
  componentCount: number
  byProductCount: number
  isFrozen: boolean
}

export interface BillComponent {
  lineNum: number
  pathRef: string | null
  componentItemCode: string
  description: string | null
  componentRevision: string | null
  usageQty: number
  usageBasis: string
  uomCode: string
  conversionFactor: number
  scrapPct: number
  supplyBehaviour: string
  lineKind: string
  isStockMaterial: boolean
  isOptional: boolean
  alternateGroup: string | null
  isSelected: boolean
  qtyPerNominalUnit: number | null
  stockUnitQty: number | null
  inputWithScrap: number | null
  needOffsetDays: number | null
  operationRef: string | null
  effectiveFrom: string | null
  effectiveTo: string | null
  notes: string | null
}

export interface BillByProduct {
  lineNum: number
  byProductItemCode: string
  description: string | null
  recoveryQty: number
  uomCode: string
  conversionFactor: number
  valuationBasis: string
  recoveryValue: number | null
  dispositionRule: string | null
  approvedBy: string | null
}

export interface WhereUsedRow {
  level: number
  bomHeaderId: number
  bomRef: string
  bomRevision: string
  status: string
  parentItemCode: string
  componentItemCode: string
  variantCode: string | null
  purpose: string
  effectiveFrom: string | null
  effectiveTo: string | null
  usageQty: number
  uomCode: string
  usageBasis: string
  isOptional: boolean
  alternateGroup: string | null
  pathText: string
  qtyPerTopUnit: number
  isDirect: boolean
}

export interface RevisionDifference {
  changeType: string
  componentItemCode: string
  alternateGroup: string | null
  fromQtyPerUnit: number | null
  toQtyPerUnit: number | null
  qtyDifference: number | null
  fromUom: string | null
  toUom: string | null
  fromBasisType: string | null
  toBasisType: string | null
  fromScrap: number | null
  toScrap: number | null
  fromSupply: string | null
  toSupply: string | null
  fromRev: string | null
  toRev: string | null
  fromEff: string | null
  toEff: string | null
  changedFields: string | null
  unitCost: number | null
  costImpact: number | null
}

// ---------------------------------------------------------------- BOM: validation, release, change preview

export interface ValidationFinding {
  severity: string
  reason: string
  lineNum: number
  item: string
  path: string
  message: string
}

export interface Validation {
  outcome: string
  errorCount: number
  warningCount: number
  releaseAllowed: boolean
  cyclePath: string
  findings: ValidationFinding[]
}

export interface ReleaseRequest {
  reviewerUser: string | null
  effectiveFrom: string | null
  supersedeRevision: string | null
  reason: string | null
}

export interface ReleaseOutcome {
  outcome: string
  bomRef: string
  bomRevision: string
  parentItemCode?: string | null
  releasedBy?: string | null
  reviewerUser?: string | null
  effectiveFrom?: string | null
  supersededRevision?: string | null
  validationId?: number | null
  resultRef?: string | null
  message: string
}

export interface BulkPreviewRequest {
  oldItem: string
  newItem: string
  effectiveDate: string
  scope: string
  scopeItem: string
  includeDraft: boolean
}

export interface BulkPreviewLine {
  bill: string
  revision: string
  proposedRevision: string
  parent: string
  treatment: string
  outcome: string
  reason: string
  detail: string
  line: number
  beforeQty: number
  afterQty: number
}

export interface BulkPreview {
  targetCount: number
  failedCount: number
  appliedCount: number
  mayApply: boolean
  summary: string
  refusal: string
  lines: BulkPreviewLine[]
}

// ---------------------------------------------------------------- other slices and the job host

export interface CostSet {
  costSet: string
  name: string
  purpose: string
  status: string
  currency: string
  fiscalYear: number | null
  effectiveFrom: string | null
  items: number
}

export interface ItemMaster {
  companyCode: string
  itemCode: string
  description: string
  itemClass: string
  category: string | null
  stockUomCode: string
  makeBuy: string
  shelfLifeDays: number | null
  traceabilityType: string | null
  valuationGroup: string | null
  lifecycleStatus: string | null
  currentRevision: string | null
  isPurchasable: boolean
  isSaleable: boolean
  isManufacturable: boolean
  isStocked: boolean
}

export interface JobRunAccepted {
  jobRunId: number
  jobName: string
  status: string
  startedAt?: string
  links?: { self?: string; sysout?: string; spool?: string; result?: string }
}

export interface JobRun {
  jobRunId: number
  jobName: string
  companyCode?: string | null
  submittedBy?: string | null
  startedAt?: string | null
  endedAt?: string | null
  status: string
  returnCode?: number | null
  logFile?: string | null
  spoolFile?: string | null
  message?: string | null
  /** Runs executed by the target job host: the run manifest (spool files, exit code, related runs). */
  execution?: {
    state?: string
    exitCode?: number | null
    commandLine?: string | null
    relatedJobRunIds?: number[]
    spoolFiles?: string[]
  } | null
}
