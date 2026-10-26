import { useMutation } from '@tanstack/react-query'
import { ArrowRight, Eraser, Replace, ScanSearch } from 'lucide-react'
import { useState } from 'react'
import { bom } from '@/api/endpoints'
import type { BulkPreview, BulkPreviewRequest, Envelope } from '@/api/types'
import { exact, fmtQty } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, Code, StatusChip, cn } from '@/components/ui/primitives'
import { DateInput, Field, Segmented, TextInput, Toggle } from '@/components/ui/forms'
import { EmptyState, Stat } from '@/components/ui/surfaces'
import { asApiError, OutcomeBanner, ProblemCard } from '@/components/ui/feedback'
import { BillPicker, ItemPicker } from '@/components/bom/Pickers'
import { PageHeader } from '@/components/shell/PageHeader'

type Scope = 'ITEM' | 'BOMREF' | 'PARENT'

export default function ChangePreviewPage() {
  const session = useSession((s) => s.session)!
  const [changeRef, setChangeRef] = useState('')
  const [oldItem, setOldItem] = useState('')
  const [newItem, setNewItem] = useState('')
  const [effective, setEffective] = useState(session.businessDate)
  const [scope, setScope] = useState<Scope>('ITEM')
  const [scopeItem, setScopeItem] = useState('')
  const [includeDraft, setIncludeDraft] = useState(true)

  const preview = useMutation({
    mutationFn: (req: { ref: string; body: BulkPreviewRequest }) => bom.previewBulkChange(req.ref, req.body),
  })
  const run = () =>
    preview.mutate({
      ref: changeRef.trim().toUpperCase(),
      body: { oldItem: oldItem.trim().toUpperCase(), newItem: newItem.trim().toUpperCase(), effectiveDate: effective, scope, scopeItem: scope === 'ITEM' ? '' : scopeItem, includeDraft },
    })
  // a refusal (422) carries the full preview in data, as the legacy screen read the COMMAREA after the refusal
  const refusal = preview.error ? asApiError(preview.error) : null
  const data: BulkPreview | undefined = preview.data?.data ?? (refusal?.problem.data as BulkPreview | undefined)

  return (
    <div className="mx-auto flex max-w-[1400px] flex-col gap-6 p-8">
      <PageHeader
        eyebrow="Engineering"
        title="Engineering change preview"
        description="Replace one component by another across bills and see, before anything is applied, which bills would get a new revision or a draft amendment and which would fail validation. The preview never changes a bill."
      />
      <div className="glass grid grid-cols-1 gap-4 rounded-3xl p-6 lg:grid-cols-4">
        <Field label="Change reference" hint="e.g. ECO-BOMDEMO-001">
          <TextInput value={changeRef} onChange={(e) => setChangeRef(e.target.value)} placeholder="ECO-…" />
        </Field>
        <Field label="Replace component">
          <ItemPicker value={oldItem} onChange={setOldItem} placeholder="Old item, e.g. DEMO-PIN" />
        </Field>
        <Field label="With">
          <ItemPicker value={newItem} onChange={setNewItem} placeholder="New item, e.g. DEMO-PIN-HD" />
        </Field>
        <Field label="Effective">
          <DateInput value={effective} onChange={(e) => setEffective(e.target.value)} />
        </Field>
        <div className="flex flex-col gap-1.5">
          <span className="text-[11px] font-semibold tracking-[0.08em] text-ink-300 uppercase">Scope</span>
          <Segmented<Scope>
            value={scope}
            onChange={setScope}
            options={[
              { value: 'ITEM', label: 'Every bill' },
              { value: 'BOMREF', label: 'One bill' },
              { value: 'PARENT', label: 'One parent' },
            ]}
          />
        </div>
        <Field label={scope === 'BOMREF' ? 'Bill reference' : scope === 'PARENT' ? 'Parent item' : 'Scope key'}>
          {scope === 'BOMREF' ? (
            <BillPicker value={scopeItem} onChange={setScopeItem} />
          ) : scope === 'PARENT' ? (
            <ItemPicker value={scopeItem} onChange={setScopeItem} />
          ) : (
            <TextInput value="" disabled placeholder="not used for every bill" />
          )}
        </Field>
        <div className="flex items-end pb-2">
          <Toggle checked={includeDraft} onChange={setIncludeDraft} label="Released and draft bills" />
        </div>
        <div className="flex items-end justify-end gap-2">
          <Button
            variant="ghost"
            icon={<Eraser className="size-4" />}
            onClick={() => {
              preview.reset()
            }}
          >
            Clear
          </Button>
          <Button variant="primary" icon={<ScanSearch className="size-4" />} loading={preview.isPending} disabled={!oldItem || !newItem} onClick={run}>
            Preview
          </Button>
        </div>
      </div>

      {refusal && <ProblemCard error={preview.error} />}
      {preview.data && <OutcomeBanner outcome={(preview.data as Envelope<BulkPreview>).outcome} />}
      {!data && !refusal && (
        <EmptyState icon={<Replace className="size-6" />} title="No preview yet">
          Try replacing <Code>DEMO-PIN</Code> with <Code>DEMO-PIN-HD</Code> in every bill, effective 1 November 2026, under change <Code>ECO-BOMDEMO-001</Code>.
        </EmptyState>
      )}
      {data && (
        <>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <Stat label="Bills in scope" value={data.targetCount} accent="#22d3ee" />
            <Stat label="Failed validation" value={<span className={data.failedCount > 0 ? 'text-rose-300' : ''}>{data.failedCount}</span>} accent="#fb7185" />
            <Stat label="Applied" value={data.appliedCount} hint="a preview applies nothing" accent="#a78bfa" />
            <Stat label="May apply" value={data.mayApply ? 'yes' : 'no'} accent="#34d399" />
          </div>
          <div className="glass rounded-2xl px-5 py-4 text-[13.5px] text-ink-200">
            {data.summary}
            {data.refusal && <p className="mt-1.5 text-rose-200">{data.refusal}</p>}
          </div>
          <div className="flex flex-col gap-2">
            {data.lines.map((l, i) => (
              <div key={i} className={cn('glass flex flex-wrap items-center gap-4 rounded-2xl px-5 py-3.5', l.outcome === 'FAILED' && 'ring-1 ring-rose-300/30')}>
                <div className="min-w-[200px]">
                  <div className="flex items-center gap-2">
                    <Code className="text-[13.5px] font-semibold">{l.bill}</Code>
                    <span className="flex items-center gap-1 font-mono text-[12px] text-ink-300">
                      {l.revision} <ArrowRight className="size-3" /> {l.proposedRevision || '–'}
                    </span>
                  </div>
                  <div className="mt-0.5 font-mono text-[11.5px] text-violet-200/80">{l.parent}</div>
                </div>
                <Badge tone={l.treatment === 'NEWREV' ? 'violet' : l.treatment === 'AMEND' ? 'sky' : 'slate'}>{l.treatment || '–'}</Badge>
                <StatusChip status={l.outcome} />
                {l.reason && <Code className="text-[12px] text-rose-200">{l.reason}</Code>}
                <span className="text-[12px] text-ink-400">line {l.line}</span>
                <span className="font-mono text-[12.5px] text-ink-200 tabular" title={`${exact(l.beforeQty)} → ${exact(l.afterQty)}`}>
                  {fmtQty(l.beforeQty)} → {fmtQty(l.afterQty)}
                </span>
                <p className="w-full text-[12.5px] text-ink-300">{l.detail}</p>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
