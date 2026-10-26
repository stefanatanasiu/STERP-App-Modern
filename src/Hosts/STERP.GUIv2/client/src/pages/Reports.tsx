import { useMutation, useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { Download, FileText, Loader2, Play, Printer, ScrollText } from 'lucide-react'
import { useMemo, useState, type ReactNode } from 'react'
import { bom, jobs } from '@/api/endpoints'
import { newInstructionKey } from '@/api/http'
import type { JobRunAccepted } from '@/api/types'
import { compactDate, fmtDateTime } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, StatusChip, cn } from '@/components/ui/primitives'
import { DateInput, Field, NumberInput, Segmented, TextInput, Toggle } from '@/components/ui/forms'
import { EmptyState, Panel, Tabs } from '@/components/ui/surfaces'
import { ProblemCard, toast } from '@/components/ui/feedback'
import { BillPicker, CostSetSelect, ItemPicker } from '@/components/bom/Pickers'
import { PageHeader } from '@/components/shell/PageHeader'

// The BOM listings run on STERP.Jobs with the legacy KEY=VALUE arguments (contracts 8.2); the run, its spool and its SYSOUT
// are the platform's job-run resources. Company, site and user are the caller's context, as the report launcher sent them.

type Explode = 'INDENT' | 'LEAF' | 'COSTED'
type Trace = 'WHEREUSED' | 'COMPARE'

export default function ReportsPage() {
  const session = useSession((s) => s.session)!
  const ctx = useSession((s) => s.context)!
  const [runs, setRuns] = useState<JobRunAccepted[]>([])
  const [active, setActive] = useState<number | null>(null)
  const common = { CO: ctx.companyCode, SITE: ctx.siteCode, USER: session.user.userCode }

  const submit = useMutation({
    mutationFn: async ({ job, params }: { job: 'BOMRP010' | 'BOMRP030'; params: Record<string, string> }) =>
      job === 'BOMRP010' ? bom.submitBomrp010({ ...common, ...params }, newInstructionKey()) : bom.submitBomrp030({ ...common, ...params }, newInstructionKey()),
    onSuccess: (run) => {
      setRuns((r) => [run, ...r])
      setActive(run.jobRunId)
      toast.info(`${run.jobName} run ${run.jobRunId} submitted`, 'Following it on STERP.Jobs…')
    },
    onError: (e) => toast.problem(e),
  })

  return (
    <div className="mx-auto flex max-w-[1500px] flex-col gap-6 p-8">
      <PageHeader
        eyebrow="Engineering"
        title="BOM reports"
        description="The bill of material listings, run as batch jobs on STERP.Jobs: indented parts list and costed explosion (BOMRP010), where-used and revision comparison (BOMRP030). Follow the run and read its 132-column spool right here."
      />
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-2">
        <ExplosionReport busy={submit.isPending} onRun={(params) => submit.mutate({ job: 'BOMRP010', params })} />
        <TraceReport busy={submit.isPending} onRun={(params) => submit.mutate({ job: 'BOMRP030', params })} />
      </div>
      {runs.length === 0 ? (
        <EmptyState icon={<Printer className="size-6" />} title="No runs yet in this session">
          Submit a report above: GUIv2 follows the job run and shows its listing when it ends.
        </EmptyState>
      ) : (
        <div className="grid grid-cols-1 gap-5 xl:grid-cols-[320px_1fr]">
          <div className="flex flex-col gap-2">
            {runs.map((r) => (
              <RunCard key={r.jobRunId} run={r} active={active === r.jobRunId} onClick={() => setActive(r.jobRunId)} />
            ))}
          </div>
          {active !== null && <RunViewer key={active} jobRunId={active} />}
        </div>
      )}
    </div>
  )
}

function ExplosionReport({ busy, onRun }: { busy: boolean; onRun: (p: Record<string, string>) => void }) {
  const session = useSession((s) => s.session)!
  const [item, setItem] = useState('FG-CAKE-CELEB-8IN')
  const [rev, setRev] = useState('')
  const [qty, setQty] = useState(12)
  const [asAt, setAsAt] = useState(session.businessDate)
  const [levels, setLevels] = useState(20)
  const [costSet, setCostSet] = useState('BOMDEMO')
  const [mode, setMode] = useState<Explode>('COSTED')
  return (
    <Panel title="Indented parts list · costed explosion" subtitle="BOMRP010 (catalogue BOMRP010, BOMRP020)" icon={<FileText className="size-4" />}>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Parent item" className="col-span-2">
          <ItemPicker value={item} onChange={setItem} />
        </Field>
        <Field label="Revision">
          <TextInput value={rev} onChange={(e) => setRev(e.target.value.toUpperCase())} placeholder="auto" maxLength={4} />
        </Field>
        <Field label="Quantity">
          <NumberInput value={qty} min={1} onChange={(e) => setQty(Number(e.target.value))} />
        </Field>
        <Field label="As at">
          <DateInput value={asAt} onChange={(e) => setAsAt(e.target.value)} />
        </Field>
        <Field label="Levels">
          <NumberInput value={levels} min={1} max={25} onChange={(e) => setLevels(Number(e.target.value))} />
        </Field>
        <div className="col-span-2 flex flex-col gap-1.5">
          <span className="text-[11px] font-semibold tracking-[0.08em] text-ink-300 uppercase">Mode</span>
          <Segmented<Explode>
            value={mode}
            onChange={setMode}
            options={[
              { value: 'INDENT', label: 'Indented' },
              { value: 'LEAF', label: 'Leaf' },
              { value: 'COSTED', label: 'Costed' },
            ]}
          />
        </div>
        {mode === 'COSTED' && (
          <Field label="Cost set" className="col-span-2">
            <CostSetSelect value={costSet} onChange={setCostSet} />
          </Field>
        )}
      </div>
      <div className="mt-5 flex justify-end">
        <Button
          variant="primary"
          icon={<Play className="size-4" />}
          loading={busy}
          disabled={!item}
          onClick={() =>
            onRun({
              ITEM: item,
              ...(rev ? { REV: rev } : {}),
              QTY: String(qty || 1),
              ASAT: compactDate(asAt),
              LEVELS: String(levels || 20),
              MODE: mode,
              ...(mode === 'COSTED' ? { COSTSET: costSet } : {}),
              reportCode: mode === 'COSTED' ? 'BOMRP020' : 'BOMRP010',
            })
          }
        >
          Run BOMRP010
        </Button>
      </div>
    </Panel>
  )
}

function TraceReport({ busy, onRun }: { busy: boolean; onRun: (p: Record<string, string>) => void }) {
  const session = useSession((s) => s.session)!
  const [mode, setMode] = useState<Trace>('WHEREUSED')
  const [item, setItem] = useState('DEMO-PIN')
  const [bill, setBill] = useState('BOM-DEMO-BDY')
  const [fromRev, setFromRev] = useState('A')
  const [toRev, setToRev] = useState('B')
  const [asAt, setAsAt] = useState(session.businessDate)
  const [levels, setLevels] = useState(25)
  const [draft, setDraft] = useState(true)
  const [costSet, setCostSet] = useState('BOMDEMO')
  return (
    <Panel title="Where used · revision comparison" subtitle="BOMRP030 (catalogue BOMRP030, BOMRP040)" icon={<ScrollText className="size-4" />}>
      <div className="grid grid-cols-2 gap-4">
        <div className="col-span-2 flex flex-col gap-1.5">
          <span className="text-[11px] font-semibold tracking-[0.08em] text-ink-300 uppercase">Mode</span>
          <Segmented<Trace>
            value={mode}
            onChange={setMode}
            options={[
              { value: 'WHEREUSED', label: 'Where used' },
              { value: 'COMPARE', label: 'Compare revisions' },
            ]}
          />
        </div>
        {mode === 'WHEREUSED' ? (
          <>
            <Field label="Component" className="col-span-2">
              <ItemPicker value={item} onChange={setItem} />
            </Field>
            <Field label="Levels">
              <NumberInput value={levels} min={1} max={50} onChange={(e) => setLevels(Number(e.target.value))} />
            </Field>
            <div className="flex items-end pb-2">
              <Toggle checked={draft} onChange={setDraft} label="Include drafts" />
            </div>
          </>
        ) : (
          <>
            <Field label="Bill" className="col-span-2">
              <BillPicker value={bill} onChange={setBill} />
            </Field>
            <Field label="From revision">
              <TextInput value={fromRev} onChange={(e) => setFromRev(e.target.value.toUpperCase())} maxLength={4} />
            </Field>
            <Field label="To revision">
              <TextInput value={toRev} onChange={(e) => setToRev(e.target.value.toUpperCase())} maxLength={4} />
            </Field>
            <Field label="Cost set" className="col-span-2">
              <CostSetSelect value={costSet} onChange={setCostSet} />
            </Field>
          </>
        )}
        <Field label="As at">
          <DateInput value={asAt} onChange={(e) => setAsAt(e.target.value)} />
        </Field>
      </div>
      <div className="mt-5 flex justify-end">
        <Button
          variant="primary"
          icon={<Play className="size-4" />}
          loading={busy}
          onClick={() =>
            onRun(
              mode === 'WHEREUSED'
                ? { MODE: 'WHEREUSED', ITEM: item, LEVELS: String(levels || 25), DRAFT: draft ? 'Y' : 'N', ASAT: compactDate(asAt), reportCode: 'BOMRP030' }
                : { MODE: 'COMPARE', BOMREF: bill, FROMREV: fromRev, TOREV: toRev, COSTSET: costSet, ASAT: compactDate(asAt), reportCode: 'BOMRP040' },
            )
          }
        >
          Run BOMRP030
        </Button>
      </div>
    </Panel>
  )
}

function useRun(jobRunId: number) {
  return useQuery({
    queryKey: ['job-run', jobRunId],
    queryFn: ({ signal }) => jobs.run(jobRunId, signal),
    refetchInterval: (q) => (q.state.data && q.state.data.status !== 'RUNNING' ? false : 1500),
  })
}

function RunCard({ run, active, onClick }: { run: JobRunAccepted; active: boolean; onClick: () => void }) {
  const live = useRun(run.jobRunId)
  const status = live.data?.status ?? run.status
  return (
    <button onClick={onClick} className={cn('glass flex cursor-pointer items-center gap-3 rounded-2xl px-4 py-3 text-left transition', active && 'ring-1 ring-violet-300/40')}>
      <span className="flex size-9 items-center justify-center rounded-xl bg-white/[0.05] ring-1 ring-white/10">
        {status === 'RUNNING' ? <Loader2 className="size-4 animate-spin text-sky-300" /> : <FileText className="size-4 text-violet-200" />}
      </span>
      <span className="min-w-0 flex-1">
        <span className="block font-mono text-[13px] text-ink-50">
          {run.jobName} #{run.jobRunId}
        </span>
        <span className="block text-[11.5px] text-ink-400">{fmtDateTime(live.data?.startedAt ?? run.startedAt)}</span>
      </span>
      <StatusChip status={status} />
    </button>
  )
}

function RunViewer({ jobRunId }: { jobRunId: number }) {
  const run = useRun(jobRunId)
  const done = !!run.data && run.data.status !== 'RUNNING'
  const [tab, setTab] = useState<'spool' | 'sysout'>('spool')
  // the listing: the run's own spool file (legacy-written runs), else the spool files the job host recorded for it
  const recorded = run.data?.execution?.spoolFiles ?? []
  const spool = useQuery({
    queryKey: ['spool', jobRunId, recorded.join('|')],
    queryFn: async ({ signal }) => {
      if (run.data?.spoolFile || recorded.length === 0) return jobs.spool(jobRunId, signal)
      const parts = await Promise.all(recorded.map((p) => jobs.spoolFile(p, signal)))
      return parts.join('\f')
    },
    enabled: done,
    retry: false,
  })
  const sysout = useQuery({ queryKey: ['sysout', jobRunId], queryFn: ({ signal }) => jobs.sysout(jobRunId, signal), enabled: done, retry: false })
  const current = tab === 'spool' ? spool : sysout
  const pages = useMemo(() => (current.data ?? '').split('\f').filter((p) => p.trim().length > 0), [current.data])
  const download = () => {
    if (!current.data) return
    const url = URL.createObjectURL(new Blob([current.data], { type: 'text/plain;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url
    a.download = `${run.data?.jobName ?? 'run'}-${jobRunId}-${tab}.txt`
    a.click()
    URL.revokeObjectURL(url)
  }
  return (
    <div className="glass flex min-h-[420px] flex-col rounded-3xl">
      <div className="flex flex-wrap items-center gap-3 px-5 pt-4">
        <div>
          <div className="flex items-center gap-2">
            <span className="font-mono text-[15px] font-semibold text-ink-50">
              {run.data?.jobName ?? '…'} #{jobRunId}
            </span>
            {run.data && <StatusChip status={run.data.status} />}
            {run.data?.returnCode !== undefined && run.data?.returnCode !== null && <Badge tone={run.data.returnCode === 0 ? 'emerald' : run.data.returnCode <= 4 ? 'amber' : 'rose'}>RC {run.data.returnCode}</Badge>}
          </div>
          <div className="text-[11.5px] text-ink-400">
            submitted by {run.data?.submittedBy ?? '–'} · {fmtDateTime(run.data?.startedAt)} → {fmtDateTime(run.data?.endedAt)}
          </div>
        </div>
        <div className="flex-1" />
        <Button size="sm" icon={<Download className="size-3.5" />} onClick={download} disabled={!current.data}>
          Download
        </Button>
      </div>
      <Tabs
        className="mt-3 px-4"
        value={tab}
        onChange={setTab}
        tabs={[
          { value: 'spool', label: 'Spool listing' },
          { value: 'sysout', label: 'SYSOUT' },
        ]}
      />
      <div className="min-h-0 flex-1 overflow-auto p-5">
        {!done ? (
          <Waiting>The job host is running the program…</Waiting>
        ) : current.error ? (
          <ProblemCard error={current.error} />
        ) : current.isPending ? (
          <Waiting>Reading the listing…</Waiting>
        ) : (
          <div className="flex flex-col items-center gap-6">
            {pages.map((p, i) => (
              <motion.pre
                key={i}
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: Math.min(i, 6) * 0.05 }}
                className="w-full max-w-[1100px] overflow-x-auto rounded-xl bg-[repeating-linear-gradient(180deg,#fbfaf6_0px,#fbfaf6_22px,#eef6ef_22px,#eef6ef_44px)] px-6 py-5 font-mono text-[11.5px] leading-[22px] text-[#1f2a1f] shadow-[0_20px_50px_-20px_rgba(0,0,0,0.8)]"
              >
                {p.replace(/^\r?\n/, '')}
              </motion.pre>
            ))}
            {pages.length === 0 && <p className="text-sm text-ink-400">The listing is empty.</p>}
          </div>
        )}
      </div>
    </div>
  )
}

function Waiting({ children }: { children: ReactNode }) {
  return (
    <div className="flex h-full min-h-[240px] items-center justify-center gap-3 text-sm text-ink-300">
      <Loader2 className="size-4 animate-spin text-violet-300" /> {children}
    </div>
  )
}
