import { useQuery } from '@tanstack/react-query'
import { motion } from 'motion/react'
import { RefreshCw, ShieldAlert } from 'lucide-react'
import { Suspense, type ReactNode } from 'react'
import { Outlet, useLocation } from 'react-router'
import { useSession } from '@/state/session'
import { signOn } from '@/state/signon'
import { Button } from '@/components/ui/primitives'
import { ProblemCard, Toaster } from '@/components/ui/feedback'
import { Aurora } from './Aurora'
import { CommandPalette } from './CommandPalette'
import { Logo, Sidebar } from './Sidebar'
import { TopBar } from './TopBar'

export function AppShell() {
  const location = useLocation()
  return (
    <SessionGate>
      <div className="flex h-full overflow-hidden">
        <Sidebar />
        <div className="flex min-w-0 flex-1 flex-col">
          <TopBar />
          <main className="relative min-h-0 flex-1 overflow-y-auto">
            <Suspense fallback={<PageLoader />}>
              <motion.div
                key={location.pathname}
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: 0.35, ease: [0.22, 1, 0.36, 1] }}
                className="h-full"
              >
                <Outlet />
              </motion.div>
            </Suspense>
          </main>
        </div>
        <CommandPalette />
      </div>
    </SessionGate>
  )
}

/** Nothing renders before the API has resolved the Windows login to a STERP user and a working context. */
function SessionGate({ children }: { children: ReactNode }) {
  const session = useSession((s) => s.session)
  const signOnQuery = useQuery({ queryKey: ['sign-on'], queryFn: signOn, retry: false, staleTime: Infinity, refetchOnWindowFocus: false })
  if (signOnQuery.isError && !session) {
    return (
      <Splash>
        <div className="flex w-[520px] max-w-[92vw] flex-col gap-4">
          <div className="flex items-center gap-3 text-ink-50">
            <ShieldAlert className="size-6 text-amber-300" />
            <h1 className="text-lg font-semibold">Sign-on did not complete</h1>
          </div>
          <ProblemCard error={signOnQuery.error} />
          <p className="text-[13px] leading-relaxed text-ink-300">
            GUIv2 signs you on with your Windows login through the STERP platform API, exactly like the classic GUI. A login without a STERP user
            answers <span className="font-mono text-ink-100">API0010</span>; <span className="font-mono text-ink-100">run-sterp.bat /reset</span> maps
            your login to SYSADM on the demo database again.
          </p>
          <div>
            <Button icon={<RefreshCw className="size-4" />} onClick={() => signOnQuery.refetch()} loading={signOnQuery.isFetching}>
              Try again
            </Button>
          </div>
        </div>
      </Splash>
    )
  }
  if (!session) {
    return (
      <Splash>
        <div className="flex flex-col items-center gap-4">
          <div className="relative size-12">
            <span className="absolute inset-0 animate-pulse-ring rounded-full bg-violet-400/40" />
            <span className="absolute inset-2 rounded-full bg-[linear-gradient(135deg,#8b5cf6,#06b6d4)]" />
          </div>
          <p className="text-sm text-ink-300">Signing on with your Windows login…</p>
        </div>
      </Splash>
    )
  }
  return (
    <>
      {children}
      <Toaster />
    </>
  )
}

function Splash({ children }: { children: ReactNode }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-10">
      <Aurora />
      <Logo />
      {children}
    </div>
  )
}

export function PageLoader() {
  return (
    <div className="flex h-full min-h-[300px] items-center justify-center">
      <div className="flex items-center gap-3 text-sm text-ink-300">
        <span className="relative size-5">
          <span className="absolute inset-0 animate-pulse-ring rounded-full bg-violet-400/50" />
          <span className="absolute inset-1 rounded-full bg-violet-400" />
        </span>
        Loading…
      </div>
    </div>
  )
}
