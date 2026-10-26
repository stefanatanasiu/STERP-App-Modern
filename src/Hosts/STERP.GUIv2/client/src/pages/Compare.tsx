import { ArrowRight, GitCompareArrows, Minus, Plus, RefreshCw } from 'lucide-react'
import { useEffect, useMemo } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { useBillRevisions, useRevisionComparison } from '@/api/hooks'
import type { RevisionDifference } from '@/api/types'
import { exact, fmtDate, fmtMoney, fmtQty } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, Code, Skeleton, StatusChip, cn } from '@/components/ui/primitives'
import { Field, Select } from '@/components/ui/forms'
import { EmptyState, Stat } from '@/components/ui/surfaces'
import { ProblemCard } from '@/components/ui/feedback'
import { BillPicker, CostSetSelect } from '@/components/bom/Pickers'
import { PageHeader } from '@/components/shell/PageHeader'

export default function ComparePage() {
  const { bomRef: refParam } = useParams()
  const [search, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const session = useSession((s) => s.session)!
  const bomRef = (refParam ?? '').trim().toUpperCase()
  const costSet = search.get('costSet') ?? 'BOMDEMO'
  const revisions = useBillRevisions(bomRef ? { bomRef, asOf: session.businessDate } : null)
  const revs = revisions.data ?? []
  const from = search.get('from') ?? ''
  const to = search.get('to') ?? ''

  // as the classic screen: the newest revision against the one before it
  useEffect(() => {
    if (revs.length >= 2 && (!from || !to)) {
      const next = new URLSearchParams(search)
      if (!to) next.set('to', revs[0].bomRevision)
      if (!from) next.set('from', revs[1].bomRevision)
      setSearch(next, { replace: true })
    }
  }, [revs, from, to, search, setSearch])

  const comparison = useRevisionComparison(bomRef || undefined, from, to, costSet)
  const rows = comparison.data ?? []
  const counts = useMemo(() => {
    const c = { ADDED: 0, REMOVED: 0, CHANGED: 0 }
    for (const r of rows) if (r.changeType in c) c[r.changeType as keyof typeof c]++
    return c
  }, [rows])
  const set = (patch: Record<string, string>) => {
    const next = new URLSearchParams(search)
    for (const [k, v] of Object.entries(patch)) next.set(k, v)
    setSearch(next, { replace: true })
  }

  return (
    <div className="mx-auto flex max-w-[1400px] flex-col gap-6 p-8">
      <PageHeader
        eyebrow="Engineering"
        title="Compare revisions"
        description="Two revisions of the same bill, side by side: what was added, removed or changed, with the cost impact the comparison procedure reports for each line."
      />
      <div className="glass flex flex-wrap items-end gap-3 rounded-3xl px-5 py-4">
        <Field label="Bill" className="w-[300px]">
          <BillPicker value={bomRef} onChange={(v) => v && navigate(`/compare/${encodeURIComponent(v)}?costSet=${encodeURIComponent(costSet)}`)} />
        </Field>
        <Field label="From revision" className="w-[200px]">
          <Select value={from} onChange={(e) => set({ from: e.target.value })} disabled={revs.length === 0}>
            <option value="">–</option>
            {revs.map((r) => (
              <option key={r.bomRevision} value={r.bomRevision}>
                {r.bomRevision} · {r.status}
              </option>
            ))}
          </Select>
        </Field>
        <ArrowRight className="mb-3 size-5 text-ink-400" />
        <Field label="To revision" className="w-[200px]">
          <Select value={to} onChange={(e) => set({ to: e.target.value })} disabled={revs.length === 0}>
            <option value="">–</option>
            {revs.map((r) => (
              <option key={r.bomRevision} value={r.bomRevision}>
                {r.bomRevision} · {r.status}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Cost set" className="w-[200px]">
          <CostSetSelect value={costSet} onChange={(v) => set({ costSet: v })} />
        </Field>
        <Button variant="primary" icon={<RefreshCw className="size-4" />} loading={comparison.isFetching} onClick={() => comparison.refetch()} disabled={!from || !to}>
          Compare
        </Button>
      </div>

      {!bomRef && (
        <EmptyState icon={<GitCompareArrows className="size-6" />} title="Pick a bill with more than one revision">
          For example <Code>BOM-DEMO-BDY</Code>: revision A is released, revision B is a draft.
        </EmptyState>
      )}
      {bomRef && revisions.isSuccess && revs.length < 2 && (
        <EmptyState icon={<GitCompareArrows className="size-6" />} title={`Bill ${bomRef} has only one revision`}>
          There is nothing to compare it with.
        </EmptyState>
      )}
      {revs.length >= 2 && (
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {[from, to].map((r, i) => {
            const rev = revs.find((x) => x.bomRevision === r)
            return (
              <div key={i} className={cn('glass rounded-2xl p-4', i === 1 && 'ring-1 ring-violet-300/25')}>
                <div className="text-[10.5px] font-semibold tracking-[0.1em] text-ink-400 uppercase">{i === 0 ? 'From' : 'To'}</div>
                {rev ? (
                  <>
                    <div className="mt-1 flex items-center gap-2">
                      <span className="font-mono text-lg font-semibold text-ink-50">rev {rev.bomRevision}</span>
                      <StatusChip status={rev.status} />
                      {rev.isEffectiveNow && <Badge tone="emerald">effective now</Badge>}
                    </div>
                    <p className="mt-1 text-[12.5px] text-ink-300">{rev.description}</p>
                    <p className="mt-1 text-[11.5px] text-ink-500">
                      {rev.componentCount} component{rev.componentCount === 1 ? '' : 's'} · from {fmtDate(rev.effectiveFrom)}
                    </p>
                  </>
                ) : (
                  <p className="mt-1 text-sm text-ink-400">–</p>
                )}
              </div>
            )
          })}
        </div>
      )}

      {comparison.error && <ProblemCard error={comparison.error} />}
      {comparison.isFetching && !comparison.data && <Skeleton className="h-48 rounded-3xl" />}
      {comparison.isSuccess && (
        <>
          <div className="grid grid-cols-3 gap-3">
            <Stat label="Added" value={<span className="text-emerald-300">{counts.ADDED}</span>} accent="#34d399" />
            <Stat label="Removed" value={<span className="text-rose-300">{counts.REMOVED}</span>} accent="#fb7185" />
            <Stat label="Changed" value={<span className="text-amber-200">{counts.CHANGED}</span>} accent="#fbbf24" />
          </div>
          {rows.length === 0 ? (
            <EmptyState title="The revisions have the same lines">The comparison procedure reports no difference.</EmptyState>
          ) : (
            <div className="flex flex-col gap-3">
              {rows.map((r, i) => (
                <DiffRow key={i} r={r} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  )
}

function DiffRow({ r }: { r: RevisionDifference }) {
  const tone = r.changeType === 'ADDED' ? 'emerald' : r.changeType === 'REMOVED' ? 'rose' : 'amber'
  const icon = r.changeType === 'ADDED' ? <Plus className="size-4" /> : r.changeType === 'REMOVED' ? <Minus className="size-4" /> : <RefreshCw className="size-4" />
  const fields = (r.changedFields ?? '').split(',').map((s) => s.trim()).filter(Boolean)
  return (
    <div className="glass flex flex-wrap items-center gap-5 rounded-2xl px-5 py-4">
      <div className={cn('flex size-10 items-center justify-center rounded-xl ring-1 ring-inset', tone === 'emerald' ? 'bg-emerald-400/10 text-emerald-300 ring-emerald-300/25' : tone === 'rose' ? 'bg-rose-400/10 text-rose-300 ring-rose-300/25' : 'bg-amber-300/10 text-amber-200 ring-amber-200/25')}>
        {icon}
      </div>
      <div className="min-w-[220px]">
        <div className="flex items-center gap-2">
          <Code className="text-[14px] font-semibold">{r.componentItemCode}</Code>
          <StatusChip status={r.changeType} />
        </div>
        <div className="mt-1 flex flex-wrap gap-1">
          {fields.map((f) => (
            <span key={f} className="rounded-md bg-white/[0.06] px-1.5 py-0.5 text-[10.5px] text-ink-300">
              {f}
            </span>
          ))}
          {r.alternateGroup && <span className="rounded-md bg-white/[0.06] px-1.5 py-0.5 text-[10.5px] text-ink-300">alt {r.alternateGroup}</span>}
        </div>
      </div>
      <div className="flex items-center gap-3 font-mono text-[14px] tabular">
        <span className={cn(r.changeType === 'ADDED' ? 'text-ink-600' : 'text-ink-200')} title={exact(r.fromQtyPerUnit)}>
          {r.fromQtyPerUnit === null ? '–' : `${fmtQty(r.fromQtyPerUnit)} ${r.fromUom ?? ''}`}
        </span>
        <ArrowRight className="size-4 text-ink-500" />
        <span className={cn(r.changeType === 'REMOVED' ? 'text-ink-600' : 'text-ink-50')} title={exact(r.toQtyPerUnit)}>
          {r.toQtyPerUnit === null ? '–' : `${fmtQty(r.toQtyPerUnit)} ${r.toUom ?? ''}`}
        </span>
        {r.qtyDifference !== null && r.qtyDifference !== 0 && (
          <span className={cn('rounded-md px-1.5 py-0.5 text-[11.5px]', r.qtyDifference > 0 ? 'bg-emerald-400/10 text-emerald-300' : 'bg-rose-400/10 text-rose-300')}>
            {r.qtyDifference > 0 ? '+' : ''}
            {fmtQty(r.qtyDifference)}
          </span>
        )}
      </div>
      <div className="grid flex-1 grid-cols-2 gap-x-5 gap-y-1 text-[11.5px] text-ink-400 md:grid-cols-4">
        <span>
          basis {r.fromBasisType ?? '–'} → {r.toBasisType ?? '–'}
        </span>
        <span>
          scrap {fmtQty(r.fromScrap)} → {fmtQty(r.toScrap)}
        </span>
        <span>
          supply {r.fromSupply ?? '–'} → {r.toSupply ?? '–'}
        </span>
        <span>
          rev {r.fromRev ?? '–'} → {r.toRev ?? '–'}
        </span>
      </div>
      <div className="text-right">
        <div className="text-[10.5px] tracking-wide text-ink-400 uppercase">Cost impact</div>
        <div className={cn('text-lg font-semibold tabular', (r.costImpact ?? 0) > 0 ? 'text-rose-200' : (r.costImpact ?? 0) < 0 ? 'text-emerald-200' : 'text-ink-100')} title={exact(r.costImpact)}>
          {r.costImpact === null ? '–' : fmtMoney(r.costImpact)}
        </div>
        <div className="text-[11px] text-ink-500">unit {r.unitCost === null ? '–' : fmtMoney(r.unitCost)}</div>
      </div>
    </div>
  )
}
