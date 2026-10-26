import { motion } from 'motion/react'
import { ArrowRight, Cake, FileText, GitCompareArrows, Library, Network, Replace, Sparkles, Waypoints } from 'lucide-react'
import { lazy, Suspense, useMemo, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { useLookup } from '@/api/hooks'
import { fmtInt } from '@/domain/format'
import { useSession } from '@/state/session'
import { Badge, Button, Skeleton, StatusChip } from '@/components/ui/primitives'

const HeroStudio = lazy(() => import('@/components/bom/HeroStudio'))

type Row = Record<string, unknown>
const s = (v: unknown) => (v === null || v === undefined ? '' : String(v))

export default function HomePage() {
  const navigate = useNavigate()
  const session = useSession((x) => x.session)!
  const bills = useLookup('BOM.HEADER', '')
  const goods = useLookup('ITM.ITEM', 'FG-')
  const rows = useMemo(() => bills.data?.items ?? [], [bills.data])
  // the featured product: a made bakery item from the item lookup (rendered as a cake by the studio)
  const featured = useMemo(() => {
    const items = goods.data?.items ?? []
    return (items.find((r) => s(r.Category) === 'BAKERY' && s(r.ItemClass) === 'MADE') ?? items.find((r) => s(r.ItemClass) === 'MADE')) as Row | undefined
  }, [goods.data])
  const featuredCode = featured ? s(featured.ItemCode) : ''
  const finished = rows.filter((r) => s(r.ParentItemCode).startsWith('FG-') && s(r.Status) === 'RELEASED')
  const stat = (status: string) => rows.filter((r) => s(r.Status) === status).length
  const parents = new Set(rows.map((r) => s(r.ParentItemCode))).size

  return (
    <div className="mx-auto flex max-w-[1500px] flex-col gap-10 p-8">
      {/* hero */}
      <section className="grid grid-cols-1 items-stretch gap-6 xl:grid-cols-[1fr_1.15fr]">
        <div className="flex flex-col justify-center py-6">
          <motion.div initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.6 }}>
            <div className="mb-4 inline-flex items-center gap-2 rounded-full bg-white/[0.05] px-3 py-1 text-[12px] text-ink-200 ring-1 ring-white/10">
              <Sparkles className="size-3.5 text-violet-300" /> GUIv2 · live on the STERP APIs · {session.selected?.companyName}
            </div>
            <h1 className="text-[52px] leading-[1.02] font-semibold tracking-[-0.035em] text-ink-50">
              See how every product
              <br />
              <span className="font-display font-medium italic text-gradient-warm">is really built.</span>
            </h1>
            <p className="mt-5 max-w-xl text-[16px] leading-relaxed text-ink-300">
              Bills of material in three dimensions: explode a product into its structure, follow every ingredient into the cake it becomes, trace a part up to
              everything that uses it — with the quantities and costs the STERP engines compute, unchanged.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              {featuredCode && (
                <Button size="lg" variant="warm" icon={<Cake className="size-5" />} onClick={() => navigate(`/studio/${encodeURIComponent(featuredCode)}`)}>
                  Open the product studio
                </Button>
              )}
              <Button size="lg" variant="primary" icon={<Network className="size-5" />} onClick={() => navigate(featuredCode ? `/explode/${encodeURIComponent(featuredCode)}` : '/explode')}>
                Explode in 3D
              </Button>
              <Button size="lg" icon={<Library className="size-5" />} onClick={() => navigate('/bills')}>
                Browse bills
              </Button>
            </div>
            <div className="mt-10 grid max-w-xl grid-cols-4 gap-3">
              <HeroStat label="Bills" value={bills.isPending ? '…' : fmtInt(rows.length)} />
              <HeroStat label="Released" value={bills.isPending ? '…' : fmtInt(stat('RELEASED'))} />
              <HeroStat label="Draft" value={bills.isPending ? '…' : fmtInt(stat('DRAFT') + stat('REVIEW'))} />
              <HeroStat label="Products" value={bills.isPending ? '…' : fmtInt(parents)} />
            </div>
          </motion.div>
        </div>
        <motion.button
          initial={{ opacity: 0, scale: 0.98 }}
          animate={{ opacity: 1, scale: 1 }}
          transition={{ duration: 0.8, delay: 0.1 }}
          onClick={() => featuredCode && navigate(`/studio/${encodeURIComponent(featuredCode)}`)}
          className="group glass relative h-[520px] cursor-pointer overflow-hidden rounded-[28px] text-left"
        >
          {featuredCode ? (
            <Suspense fallback={<Skeleton className="size-full rounded-none" />}>
              <HeroStudio item={featuredCode} />
            </Suspense>
          ) : (
            <Skeleton className="size-full rounded-none" />
          )}
          <div className="pointer-events-none absolute inset-x-0 bottom-0 flex items-end justify-between gap-4 bg-[linear-gradient(0deg,rgba(5,7,13,0.85),transparent)] p-6">
            <div>
              <div className="text-[11px] font-semibold tracking-[0.14em] text-pink-200 uppercase">Featured · {s(featured?.Category)}</div>
              <div className="mt-1 font-display text-2xl font-semibold text-ink-50 italic">{s(featured?.Description).split(',')[0]}</div>
              <div className="font-mono text-[12px] text-ink-300">{featuredCode}</div>
            </div>
            <span className="flex items-center gap-2 rounded-full bg-white/10 px-4 py-2 text-[13px] text-white ring-1 ring-white/20 backdrop-blur transition group-hover:bg-white/20">
              Enter the studio <ArrowRight className="size-4" />
            </span>
          </div>
        </motion.button>
      </section>

      {/* features */}
      <section>
        <SectionHeader title="Explore" subtitle="Every view is an existing STERP operation, drawn anew." />
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          <Feature icon={<Network />} title="Explosion 3D" text="A clickable 3D tree of the whole structure: cone, orbit or tiers, with costs and netting." api="bom_explodeBill · bom_costBill · bom_netBillRequirement" go={() => navigate('/explode/FG-BIKE-COMMUTE-M?qty=10&mode=cost')} tint="#a78bfa" />
          <Feature icon={<Cake />} title="Product studio" text="The cake rendered layer by layer, every ingredient on the table flowing into the part it becomes." api="bom_explodeBill LEAF · bom_costBill" go={() => featuredCode && navigate(`/studio/${encodeURIComponent(featuredCode)}`)} tint="#f9a8d4" />
          <Feature icon={<Waypoints />} title="Where used" text="Grow a component upwards into every assembly and product that uses it." api="bom_getWhereUsed" go={() => navigate('/where-used/DEMO-PIN')} tint="#22d3ee" />
          <Feature icon={<GitCompareArrows />} title="Compare revisions" text="Added, removed and changed lines between two revisions, with the cost impact." api="bom_compareBillRevisions" go={() => navigate('/compare/BOM-DEMO-BDY')} tint="#fbbf24" />
          <Feature icon={<Replace />} title="Change preview" text="Replace a component across bills and preview new revisions, amendments and refusals." api="bom_previewBulkChange" go={() => navigate('/changes')} tint="#34d399" />
          <Feature icon={<FileText />} title="Reports" text="Run the BOM listings on the job host and read the spool as printed pages." api="BOMRP010 · BOMRP030 on STERP.Jobs" go={() => navigate('/reports')} tint="#c4b5fd" />
        </div>
      </section>

      {/* finished goods */}
      <section>
        <SectionHeader title="Finished goods" subtitle="Released bills of finished products, from the BOM.HEADER lookup." />
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {bills.isPending && [0, 1].map((i) => <Skeleton key={i} className="h-24 rounded-2xl" />)}
          {finished.map((r) => (
            <div key={`${s(r.BomRef)}-${s(r.BomRevision)}`} className="glass flex items-center gap-4 rounded-2xl p-4">
              <div className="flex size-12 items-center justify-center rounded-2xl bg-[linear-gradient(135deg,rgba(139,92,246,0.3),rgba(6,182,212,0.2))] ring-1 ring-white/10">
                {s(r.ParentItemCode).includes('CAKE') ? <Cake className="size-5 text-pink-200" /> : <Network className="size-5 text-violet-100" />}
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-[14px] text-ink-50">{s(r.ParentItemCode)}</span>
                  <StatusChip status={s(r.Status)} />
                </div>
                <p className="truncate text-[12.5px] text-ink-400">{s(r.Description)}</p>
                <Badge tone="violet" className="mt-1">
                  {s(r.BomRef)} rev {s(r.BomRevision)}
                </Badge>
              </div>
              <div className="flex flex-col gap-1.5">
                <Button size="sm" variant="primary" icon={<Network className="size-3.5" />} onClick={() => navigate(`/explode/${encodeURIComponent(s(r.ParentItemCode))}`)}>
                  Explode
                </Button>
                <Button size="sm" icon={<Cake className="size-3.5" />} onClick={() => navigate(`/studio/${encodeURIComponent(s(r.ParentItemCode))}`)}>
                  Studio
                </Button>
              </div>
            </div>
          ))}
        </div>
      </section>
    </div>
  )
}

function HeroStat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-2xl bg-white/[0.04] px-3 py-3 ring-1 ring-white/[0.08] ring-inset">
      <div className="text-[22px] font-semibold tracking-tight text-ink-50 tabular">{value}</div>
      <div className="text-[11px] tracking-wide text-ink-400 uppercase">{label}</div>
    </div>
  )
}

function SectionHeader({ title, subtitle }: { title: string; subtitle: string }) {
  return (
    <div className="mb-4 flex items-end gap-3">
      <h2 className="text-xl font-semibold tracking-tight text-ink-50">{title}</h2>
      <span className="pb-0.5 text-[13px] text-ink-400">{subtitle}</span>
    </div>
  )
}

function Feature({ icon, title, text, api, go, tint }: { icon: ReactNode; title: string; text: string; api: string; go: () => void; tint: string }) {
  return (
    <motion.button
      whileHover={{ y: -3 }}
      onClick={go}
      className="group glass relative flex cursor-pointer flex-col overflow-hidden rounded-2xl p-5 text-left"
    >
      <span className="pointer-events-none absolute -top-20 -right-20 size-48 rounded-full opacity-25 blur-3xl transition group-hover:opacity-50" style={{ background: tint }} />
      <span className="flex size-11 items-center justify-center rounded-2xl ring-1 ring-white/15 [&>svg]:size-5" style={{ background: `${tint}26`, color: tint }}>
        {icon}
      </span>
      <h3 className="mt-4 text-[16px] font-semibold text-ink-50">{title}</h3>
      <p className="mt-1.5 text-[13px] leading-relaxed text-ink-300">{text}</p>
      <div className="mt-4 flex items-center justify-between gap-3">
        <span className="font-mono text-[10.5px] text-ink-500">{api}</span>
        <ArrowRight className="size-4 text-ink-500 transition group-hover:translate-x-0.5 group-hover:text-ink-100" />
      </div>
    </motion.button>
  )
}
