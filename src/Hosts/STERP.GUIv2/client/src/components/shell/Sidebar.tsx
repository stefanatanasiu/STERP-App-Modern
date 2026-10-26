import { motion } from 'motion/react'
import { NavLink } from 'react-router'
import {
  Cake,
  ExternalLink,
  FileText,
  GitCompareArrows,
  LayoutDashboard,
  Library,
  Network,
  Replace,
  Terminal,
  Waypoints,
} from 'lucide-react'
import type { ReactNode } from 'react'
import { useHostConfig } from '@/api/host'
import { cn } from '@/components/ui/primitives'

interface NavItem {
  to: string
  label: string
  icon: ReactNode
  end?: boolean
}

export const NAV: { section: string; items: NavItem[] }[] = [
  {
    section: 'Bills of material',
    items: [
      { to: '/', label: 'Overview', icon: <LayoutDashboard className="size-[18px]" />, end: true },
      { to: '/bills', label: 'Bill catalogue', icon: <Library className="size-[18px]" /> },
      { to: '/explode', label: 'Explosion 3D', icon: <Network className="size-[18px]" /> },
      { to: '/studio', label: 'Product studio', icon: <Cake className="size-[18px]" /> },
    ],
  },
  {
    section: 'Engineering',
    items: [
      { to: '/where-used', label: 'Where used', icon: <Waypoints className="size-[18px]" /> },
      { to: '/compare', label: 'Compare revisions', icon: <GitCompareArrows className="size-[18px]" /> },
      { to: '/changes', label: 'Change preview', icon: <Replace className="size-[18px]" /> },
      { to: '/reports', label: 'Reports', icon: <FileText className="size-[18px]" /> },
    ],
  },
]

export function Logo({ compact }: { compact?: boolean }) {
  return (
    <div className="flex items-center gap-3">
      <div className="relative flex size-10 shrink-0 items-center justify-center rounded-2xl bg-[conic-gradient(from_210deg,#8b5cf6,#06b6d4,#ec4899,#f59e0b,#8b5cf6)] p-px shadow-[0_8px_30px_-6px_rgba(139,92,246,0.7)]">
        <div className="flex size-full items-center justify-center rounded-[15px] bg-ink-900">
          <svg viewBox="0 0 24 24" className="size-5" fill="none" stroke="url(#lg)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <defs>
              <linearGradient id="lg" x1="0" y1="0" x2="1" y2="1">
                <stop offset="0" stopColor="#c4b5fd" />
                <stop offset="1" stopColor="#67e8f9" />
              </linearGradient>
            </defs>
            <circle cx="12" cy="5" r="2.2" />
            <circle cx="5.5" cy="18" r="2.2" />
            <circle cx="12" cy="18" r="2.2" />
            <circle cx="18.5" cy="18" r="2.2" />
            <path d="M12 7.2v8.6M12 10.5l-6.5 5.3M12 10.5l6.5 5.3" />
          </svg>
        </div>
      </div>
      {!compact && (
        <div className="hidden min-w-0 xl:block">
          <div className="flex items-center gap-2">
            <span className="text-[17px] font-semibold tracking-tight text-ink-50">STERP</span>
            <span className="rounded-md bg-[linear-gradient(90deg,#8b5cf6,#06b6d4)] px-1.5 py-px text-[10px] font-bold tracking-wider text-white">GUIv2</span>
          </div>
          <div className="truncate text-[11px] text-ink-400">Supply chain management</div>
        </div>
      )}
    </div>
  )
}

export function Sidebar() {
  const config = useHostConfig().data
  return (
    <aside className="relative z-20 flex w-[76px] shrink-0 flex-col border-r border-white/[0.06] bg-ink-950/40 backdrop-blur-xl xl:w-[256px]">
      <div className="flex h-16 items-center px-[18px]">
        <Logo />
      </div>
      <nav className="no-scrollbar flex-1 overflow-y-auto px-3 pt-2 pb-4">
        {NAV.map((group) => (
          <div key={group.section} className="mt-4 first:mt-1">
            <div className="hidden px-3 pb-2 text-[10.5px] font-semibold tracking-[0.14em] text-ink-400 uppercase xl:block">{group.section}</div>
            <ul className="flex flex-col gap-0.5">
              {group.items.map((item) => (
                <li key={item.to}>
                  <NavLink
                    to={item.to}
                    end={item.end}
                    className={({ isActive }) =>
                      cn(
                        'group relative flex h-10 items-center gap-3 rounded-xl px-3 text-[13.5px] font-medium transition-colors',
                        isActive ? 'text-ink-50' : 'text-ink-300 hover:bg-white/[0.04] hover:text-ink-100',
                      )
                    }
                    title={item.label}
                  >
                    {({ isActive }) => (
                      <>
                        {isActive && (
                          <motion.span
                            layoutId="nav-active"
                            transition={{ type: 'spring', stiffness: 520, damping: 40 }}
                            className="absolute inset-0 rounded-xl bg-[linear-gradient(90deg,rgba(139,92,246,0.22),rgba(14,165,233,0.08))] ring-1 ring-white/10 ring-inset"
                          >
                            <span className="absolute top-2 bottom-2 left-0 w-[3px] rounded-full bg-[linear-gradient(180deg,#c4b5fd,#22d3ee)]" />
                          </motion.span>
                        )}
                        <span className={cn('relative z-10 transition-colors', isActive ? 'text-violet-200' : 'text-ink-400 group-hover:text-ink-200')}>
                          {item.icon}
                        </span>
                        <span className="relative z-10 hidden truncate xl:inline">{item.label}</span>
                      </>
                    )}
                  </NavLink>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>
      <div className="border-t border-white/[0.06] p-3">
        {config && (
          <div className="flex flex-col gap-0.5">
            <ExternalNav href={config.classicGuiUrl} label="Classic GUI" icon={<ExternalLink className="size-4" />} />
            <ExternalNav href={config.apiConsoleUrl} label="API Console" icon={<Terminal className="size-4" />} />
            <div className="hidden px-3 pt-2 text-[10.5px] text-ink-500 xl:block">GUIv2 {config.version} · live STERP APIs</div>
          </div>
        )}
      </div>
    </aside>
  )
}

function ExternalNav({ href, label, icon }: { href: string; label: string; icon: ReactNode }) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noreferrer"
      title={label}
      className="flex h-9 items-center gap-3 rounded-xl px-3 text-[13px] text-ink-300 transition-colors hover:bg-white/[0.04] hover:text-ink-100"
    >
      <span className="text-ink-400">{icon}</span>
      <span className="hidden xl:inline">{label}</span>
    </a>
  )
}
