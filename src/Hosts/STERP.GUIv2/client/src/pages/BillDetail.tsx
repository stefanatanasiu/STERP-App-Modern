import { useMutation, useQueryClient } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { BadgeCheck, Cake, CheckCircle2, GitCompareArrows, GitFork, History, Library, Network, Rocket, ShieldCheck, TriangleAlert } from 'lucide-react'
import { useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { bom } from '@/api/endpoints'
import { useBillByProducts, useBillComponents, useBillHeader, useBillRevisions } from '@/api/hooks'
import { asApiError, OutcomeBanner, ProblemCard, toast } from '@/components/ui/feedback'
import { newInstructionKey } from '@/api/http'
import type { BillHeader, Envelope, Validation } from '@/api/types'
import { exact, fmtDate, fmtDateTime, fmtMoney, fmtPct, fmtQty, todayIso } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, Code, Skeleton, StatusChip, cn } from '@/components/ui/primitives'
import { DateInput, Field, TextInput } from '@/components/ui/forms'
import { Dialog, EmptyState, KeyValues, Panel, Stat, Tabs } from '@/components/ui/surfaces'
import { PageHeader } from '@/components/shell/PageHeader'

type Tab = 'components' | 'byproducts' | 'revisions' | 'validation'

export default function BillDetailPage() {
  const { bomRef = '' } = useParams()
  const [search, setSearch] = useSearchParams()
  const navigate = useNavigate()
  const session = useSession((s) => s.session)!
  const asOf = session.businessDate
  const rev = search.get('rev') ?? ''
  const header = useBillHeader(bomRef, rev, asOf)
  const h = header.data
  const revision = h?.bomRevision ?? rev
  const components = useBillComponents(bomRef, revision, asOf)
  const byProducts = useBillByProducts(bomRef, revision, asOf)
  const revisions = useBillRevisions({ bomRef, asOf })
  const [tab, setTab] = useState<Tab>('components')
  const [validation, setValidation] = useState<{ result?: Envelope<Validation>; error?: unknown } | null>(null)
  const [releasing, setReleasing] = useState(false)

  const validate = useMutation({
    mutationFn: () => bom.validate(bomRef, { bomRevision: revision, effectiveDate: todayIso() }),
    onSuccess: (result) => setValidation({ result }),
    onError: (error) => setValidation({ error }),
    onSettled: () => setTab('validation'),
  })

  const parent = h?.parentItemCode
  return (
    <div className="mx-auto flex max-w-[1500px] flex-col gap-6 p-8">
      <PageHeader
        eyebrow={
          <button onClick={() => navigate('/bills')} className="cursor-pointer hover:text-violet-200">
            Bill catalogue /
          </button>
        }
        title={
          <span className="flex flex-wrap items-center gap-3">
            <span className="font-mono">{bomRef}</span>
            {revision && <span className="rounded-lg bg-white/[0.07] px-2 py-0.5 font-mono text-[15px] text-ink-200">rev {revision}</span>}
            {h && <StatusChip status={h.status} className="text-[12px]" />}
          </span>
        }
        description={h?.description ?? (header.isPending ? 'Loading the bill header…' : undefined)}
        actions={
          parent && (
            <>
              <Button variant="primary" icon={<Network className="size-4" />} onClick={() => navigate(`/explode/${encodeURIComponent(parent)}${revision ? `?rev=${encodeURIComponent(revision)}` : ''}`)}>
                Explode in 3D
              </Button>
              <Button variant="warm" icon={<Cake className="size-4" />} onClick={() => navigate(`/studio/${encodeURIComponent(parent)}`)}>
                Studio
              </Button>
              <Button icon={<GitCompareArrows className="size-4" />} onClick={() => navigate(`/compare/${encodeURIComponent(bomRef)}`)}>
                Compare
              </Button>
              <Button icon={<GitFork className="size-4" />} onClick={() => navigate(`/where-used/${encodeURIComponent(parent)}`)}>
                Where used
              </Button>
            </>
          )
        }
      />

      {header.error && <ProblemCard error={header.error} />}
      {header.isPending && <Skeleton className="h-40 rounded-3xl" />}
      {!header.isPending && !h && !header.error && (
        <EmptyState icon={<Library className="size-6" />} title="No bill revision found">
          bom_getBillHeader returned no row for {bomRef} {rev && `revision ${rev}`} as at {fmtDate(asOf)}.
        </EmptyState>
      )}

      {h && (
        <>
          <HeaderPanel h={h} />
          <div className="flex flex-wrap items-center gap-2">
            <Button icon={<ShieldCheck className="size-4" />} loading={validate.isPending} onClick={() => validate.mutate()}>
              Validate
            </Button>
            <Button variant="secondary" icon={<Rocket className="size-4" />} onClick={() => setReleasing(true)}>
              Release…
            </Button>
            <span className="text-[12px] text-ink-400">Validation and release are the API's own checks (bom_validateBill, bom_releaseBillRevision).</span>
          </div>

          <div className="glass rounded-3xl">
            <Tabs
              className="px-4 pt-2"
              value={tab}
              onChange={setTab}
              tabs={[
                { value: 'components', label: 'Components', count: components.data?.length },
                { value: 'byproducts', label: 'By-products', count: byProducts.data?.length },
                { value: 'revisions', label: 'Revisions', count: revisions.data?.length },
                { value: 'validation', label: 'Validation' },
              ]}
            />
            <div className="p-4">
              {tab === 'components' && <ComponentsTable query={components} onItem={(code) => navigate(`/explode/${encodeURIComponent(code)}`)} />}
              {tab === 'byproducts' && <ByProductsTable query={byProducts} />}
              {tab === 'revisions' && <RevisionsTimeline query={revisions} current={revision} onPick={(r) => setSearch({ rev: r })} />}
              {tab === 'validation' && <ValidationResult state={validation} />}
            </div>
          </div>
          <ReleaseDialog open={releasing} onClose={() => setReleasing(false)} header={h} />
        </>
      )}
    </div>
  )
}

function HeaderPanel({ h }: { h: BillHeader }) {
  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[1fr_420px]">
      <div className="grid grid-cols-2 content-start gap-3 md:grid-cols-3">
        <Stat label="Parent item" value={<span className="font-mono text-[15px]">{h.parentItemCode}</span>} hint={h.parentDescription ?? undefined} accent="#a78bfa" title={h.parentItemCode} />
        <Stat label="Nominal basis" value={fmtQty(h.nominalBasis)} hint={`${h.stockUom ?? ''} of parent per bill`} title={exact(h.nominalBasis)} accent="#22d3ee" />
        <Stat label="Yield" value={fmtPct(h.yieldFactor)} hint={`factor ${fmtQty(h.yieldFactor)}`} title={exact(h.yieldFactor)} accent="#34d399" />
        <Stat label="Quantum" value={fmtQty(h.productionQuantum)} hint="production steps" accent="#fbbf24" />
        <Stat label="Batch size" value={h.batchSize === null ? '–' : fmtQty(h.batchSize)} hint={h.costingLotSize ? `costing lot ${fmtQty(h.costingLotSize)}` : 'costing lot –'} accent="#f9a8d4" />
        <Stat label="Lines" value={`${h.componentCount} · ${h.byProductCount}`} hint="components · by-products" accent="#c4b5fd" />
      </div>
      <Panel title="Release and effectivity" icon={<BadgeCheck className="size-4" />}>
        <KeyValues
          items={[
            ['Effective', `${fmtDate(h.effectiveFrom)} → ${h.effectiveTo ? fmtDate(h.effectiveTo) : 'open'}`],
            ['Purpose', `${h.purpose}${h.variantCode ? ` · ${h.variantCode}` : ''}`],
            ['Author', h.authorUser ?? '–'],
            ['Reviewer', h.reviewerUser ?? '–'],
            ['Released', h.releasedBy ? `${h.releasedBy} · ${fmtDateTime(h.releasedAt)}` : '–'],
            ['Frozen', h.isFrozen ? 'yes (released, superseded or retired)' : 'no'],
            ['Change', h.changeRef ?? '–'],
            ['Phantom', h.isPhantom ? 'yes' : 'no'],
          ]}
        />
        {(h.changeReason || h.engineeringNotes) && (
          <div className="mt-3 rounded-xl bg-white/[0.03] p-3 text-[12.5px] text-ink-300 ring-1 ring-white/[0.06] ring-inset">
            {h.changeReason && <p>{h.changeReason}</p>}
            {h.engineeringNotes && <p className="mt-1 text-ink-400">{h.engineeringNotes}</p>}
          </div>
        )}
      </Panel>
    </div>
  )
}

function ComponentsTable({ query, onItem }: { query: ReturnType<typeof useBillComponents>; onItem: (code: string) => void }) {
  if (query.error) return <ProblemCard error={query.error} />
  if (query.isPending) return <Skeleton className="h-64" />
  const rows = query.data ?? []
  if (rows.length === 0) return <EmptyState title="No component lines" />
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[12.5px]">
        <thead className="text-left text-[10.5px] font-semibold tracking-[0.08em] text-ink-400 uppercase">
          <tr>
            {['Line', 'Component', 'Usage', 'Basis', 'Scrap', 'Supply', 'Kind', 'Stock qty', 'With scrap', 'Options', 'Notes'].map((t) => (
              <th key={t} className="px-3 py-2.5 whitespace-nowrap">
                {t}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((c) => (
            <tr key={`${c.lineNum}-${c.componentItemCode}`} className="border-t border-white/[0.05] hover:bg-white/[0.03]">
              <td className="px-3 py-2 font-mono text-ink-400">{c.lineNum}</td>
              <td className="px-3 py-2">
                <button onClick={() => onItem(c.componentItemCode)} className="cursor-pointer text-left">
                  <span className="block font-mono text-ink-50 hover:text-violet-200">{c.componentItemCode}</span>
                  <span className="block max-w-[280px] truncate text-[11.5px] text-ink-400">{c.description}</span>
                </button>
              </td>
              <td className="px-3 py-2 whitespace-nowrap text-ink-100 tabular" title={exact(c.usageQty)}>
                {fmtQty(c.usageQty)} <span className="text-[11px] text-ink-400">{c.uomCode}</span>
              </td>
              <td className="px-3 py-2 text-ink-300">{c.usageBasis}</td>
              <td className="px-3 py-2 text-ink-300 tabular" title={exact(c.scrapPct)}>
                {c.scrapPct ? fmtPct(c.scrapPct) : '–'}
              </td>
              <td className="px-3 py-2">
                <Badge tone={c.supplyBehaviour === 'MAKE' ? 'violet' : c.supplyBehaviour === 'PHANTOM' ? 'slate' : c.supplyBehaviour === 'SERVICE' ? 'emerald' : 'cyan'}>
                  {c.supplyBehaviour}
                </Badge>
              </td>
              <td className="px-3 py-2 text-ink-300">{c.lineKind}</td>
              <td className="px-3 py-2 text-ink-300 tabular" title={exact(c.stockUnitQty)}>
                {fmtQty(c.stockUnitQty)}
              </td>
              <td className="px-3 py-2 text-ink-300 tabular" title={exact(c.inputWithScrap)}>
                {fmtQty(c.inputWithScrap)}
              </td>
              <td className="px-3 py-2 text-[11.5px] text-ink-400">
                {c.isOptional ? 'optional ' : ''}
                {c.alternateGroup ? `alt ${c.alternateGroup}${c.isSelected ? ' (selected)' : ''}` : ''}
                {!c.isOptional && !c.alternateGroup ? '–' : ''}
              </td>
              <td className="max-w-[300px] px-3 py-2 text-[11.5px] text-ink-300">{c.notes ?? ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ByProductsTable({ query }: { query: ReturnType<typeof useBillByProducts> }) {
  if (query.error) return <ProblemCard error={query.error} />
  if (query.isPending) return <Skeleton className="h-32" />
  const rows = query.data ?? []
  if (rows.length === 0) return <EmptyState title="No by-products" />
  return (
    <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
      {rows.map((b) => (
        <div key={b.lineNum} className="rounded-2xl bg-amber-300/[0.05] p-4 ring-1 ring-amber-200/15 ring-inset">
          <div className="flex items-center gap-2">
            <Badge tone="amber">By-product</Badge>
            <Code>{b.byProductItemCode}</Code>
            <span className="ml-auto text-[11px] text-ink-400">line {b.lineNum}</span>
          </div>
          <p className="mt-2 text-[13px] text-ink-200">{b.description}</p>
          <div className="mt-3 grid grid-cols-3 gap-3 text-[12.5px]">
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Recovery</div>
              <div className="text-ink-50 tabular">
                {fmtQty(b.recoveryQty)} {b.uomCode}
              </div>
            </div>
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Valuation</div>
              <div className="text-ink-50">{b.valuationBasis}</div>
            </div>
            <div>
              <div className="text-[10.5px] text-ink-400 uppercase">Value</div>
              <div className="text-ink-50 tabular">{b.recoveryValue === null ? '–' : fmtMoney(b.recoveryValue)}</div>
            </div>
          </div>
          {b.dispositionRule && <p className="mt-3 text-[12px] text-ink-400">{b.dispositionRule}</p>}
        </div>
      ))}
    </div>
  )
}

function RevisionsTimeline({ query, current, onPick }: { query: ReturnType<typeof useBillRevisions>; current: string; onPick: (rev: string) => void }) {
  if (query.error) return <ProblemCard error={query.error} />
  if (query.isPending) return <Skeleton className="h-32" />
  const rows = query.data ?? []
  return (
    <ol className="relative ml-3 border-l border-white/10 pl-6">
      {rows.map((r, i) => (
        <motion.li key={`${r.bomRevision}-${r.bomHeaderId}`} initial={{ opacity: 0, x: -6 }} animate={{ opacity: 1, x: 0 }} transition={{ delay: i * 0.05 }} className="relative mb-5 last:mb-0">
          <span className={cn('absolute top-1.5 -left-[31px] size-3 rounded-full ring-4 ring-ink-900', r.isEffectiveNow ? 'bg-emerald-400' : 'bg-ink-500')} />
          <button onClick={() => onPick(r.bomRevision)} className={cn('w-full cursor-pointer rounded-2xl p-4 text-left ring-1 transition ring-inset', r.bomRevision === current ? 'bg-violet-500/10 ring-violet-300/30' : 'bg-white/[0.03] ring-white/[0.07] hover:bg-white/[0.06]')}>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-mono text-[14px] font-semibold text-ink-50">rev {r.bomRevision}</span>
              <StatusChip status={r.status} />
              {r.isEffectiveNow && <Badge tone="emerald">effective now</Badge>}
              {r.selectionRank !== null && <span className="text-[11px] text-ink-400">selection rank {r.selectionRank}</span>}
              <span className="ml-auto text-[11.5px] text-ink-400">
                {fmtDate(r.effectiveFrom)} → {r.effectiveTo ? fmtDate(r.effectiveTo) : 'open'}
              </span>
            </div>
            <p className="mt-1.5 text-[12.5px] text-ink-300">{r.description}</p>
            <p className="mt-1 text-[11.5px] text-ink-500">
              {r.componentCount} components · author {r.authorUser ?? '–'} · {r.releasedBy ? `released by ${r.releasedBy} ${fmtDateTime(r.releasedAt)}` : 'not released'}
            </p>
          </button>
        </motion.li>
      ))}
    </ol>
  )
}

function ValidationResult({ state }: { state: { result?: Envelope<Validation>; error?: unknown } | null }) {
  if (!state) {
    return (
      <EmptyState icon={<ShieldCheck className="size-6" />} title="Not validated yet">
        Validate runs the release gate's rule set without recording anything (BOMVALID CHECK).
      </EmptyState>
    )
  }
  if (state.error) {
    const e = asApiError(state.error)
    const data = e.problem.data as Validation | undefined
    return (
      <div className="flex flex-col gap-3">
        <ProblemCard error={state.error} />
        {data && <ValidationSummary v={data} />}
        <p className="text-[12px] text-ink-400">This is the answer of bom_validateBill, shown unchanged.</p>
      </div>
    )
  }
  const v = state.result!.data
  return (
    <div className="flex flex-col gap-4">
      <OutcomeBanner outcome={state.result!.outcome} />
      <ValidationSummary v={v} />
      {v.findings.length > 0 && (
        <div className="flex flex-col gap-1.5">
          {v.findings.map((f, i) => (
            <div key={i} className="flex items-start gap-3 rounded-xl bg-white/[0.03] px-3 py-2.5 ring-1 ring-white/[0.06] ring-inset">
              {f.severity.startsWith('E') ? <TriangleAlert className="mt-0.5 size-4 text-rose-300" /> : <CheckCircle2 className="mt-0.5 size-4 text-amber-300" />}
              <div className="min-w-0 text-[12.5px]">
                <div className="flex items-center gap-2">
                  <Code className="text-[11.5px]">{f.reason}</Code>
                  <span className="text-ink-400">line {f.lineNum}</span>
                  <Code className="text-[11.5px] text-ink-300">{f.item}</Code>
                </div>
                <p className="mt-0.5 text-ink-200">{f.message}</p>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function ValidationSummary({ v }: { v: Validation }) {
  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
      <Stat label="Outcome" value={<StatusChip status={v.outcome} className="text-[12px]" />} />
      <Stat label="Errors" value={v.errorCount} />
      <Stat label="Warnings" value={v.warningCount} />
      <Stat label="Release" value={v.releaseAllowed ? 'allowed' : 'blocked'} hint={v.cyclePath || undefined} />
    </div>
  )
}

function ReleaseDialog({ open, onClose, header }: { open: boolean; onClose: () => void; header: BillHeader }) {
  const queryClient = useQueryClient()
  const [reviewer, setReviewer] = useState('')
  const [reason, setReason] = useState('Engineering release')
  const [effective, setEffective] = useState(todayIso())
  const [supersede, setSupersede] = useState('')
  const [key] = useState(newInstructionKey)
  const released = header.status === 'RELEASED'
  const release = useMutation({
    mutationFn: () =>
      bom.release(
        header.bomRef,
        header.bomRevision,
        { reviewerUser: reviewer.trim().toUpperCase() || null, effectiveFrom: effective || null, supersedeRevision: supersede.trim().toUpperCase() || null, reason: reason.trim() || null },
        key,
      ),
    onSuccess: async (rows) => {
      const row = rows[0]
      toast.success(row ? `${row.outcome}: ${header.bomRef} rev ${header.bomRevision}` : 'Release accepted', row?.message)
      await queryClient.invalidateQueries({ queryKey: ['bom'] })
      onClose()
    },
  })
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={`Release ${header.bomRef} revision ${header.bomRevision}`}
      subtitle="The release gate (bom.usp_ReleaseBom) decides: reviewer rules, authority and validation are enforced by the API."
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" icon={<Rocket className="size-4" />} loading={release.isPending} disabled={released || !reviewer.trim()} onClick={() => release.mutate()}>
            Release
          </Button>
        </>
      }
    >
      {released ? (
        <div className="flex items-start gap-3 rounded-2xl bg-sky-400/[0.07] p-4 text-[13px] text-sky-100 ring-1 ring-sky-300/20 ring-inset">
          <History className="mt-0.5 size-4" />
          Revision {header.bomRevision} is already released: supersede it with a new revision.
        </div>
      ) : (
        <div className="grid grid-cols-2 gap-4">
          <Field label="Reviewer" hint="The reviewer may be neither the author nor you." className="col-span-2">
            <TextInput value={reviewer} onChange={(e) => setReviewer(e.target.value)} placeholder="User code, e.g. ENGMGR" autoFocus />
          </Field>
          <Field label="Reason" className="col-span-2">
            <TextInput value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
          <Field label="Effective from">
            <DateInput value={effective} onChange={(e) => setEffective(e.target.value)} />
          </Field>
          <Field label="Supersede revision" hint="optional">
            <TextInput value={supersede} onChange={(e) => setSupersede(e.target.value)} maxLength={4} />
          </Field>
        </div>
      )}
      {release.error && <ProblemCard error={release.error} className="mt-4" />}
    </Dialog>
  )
}
