import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { lazy } from 'react'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { ApiError } from '@/api/http'
import { AppShell } from '@/components/shell/AppShell'

// the 3D pages carry three.js: loaded on first visit only
const Home = lazy(() => import('@/pages/Home'))
const Bills = lazy(() => import('@/pages/Bills'))
const BillDetail = lazy(() => import('@/pages/BillDetail'))
const Explosion = lazy(() => import('@/pages/Explosion'))
const Studio = lazy(() => import('@/pages/Studio'))
const WhereUsed = lazy(() => import('@/pages/WhereUsed'))
const Compare = lazy(() => import('@/pages/Compare'))
const ChangePreview = lazy(() => import('@/pages/ChangePreview'))
const Reports = lazy(() => import('@/pages/Reports'))
const NotFound = lazy(() => import('@/pages/NotFound'))

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // a business refusal (4xx) is the legacy answer: asking again gives the same answer
      retry: (count, error) => count < 1 && (!(error instanceof ApiError) || error.status === 0 || error.status >= 502),
      refetchOnWindowFocus: false,
    },
  },
})

const router = createBrowserRouter([
  {
    path: '/',
    element: <AppShell />,
    children: [
      { index: true, element: <Home /> },
      { path: 'bills', element: <Bills /> },
      { path: 'bills/:bomRef', element: <BillDetail /> },
      { path: 'explode/:item?', element: <Explosion /> },
      { path: 'studio/:item?', element: <Studio /> },
      { path: 'where-used/:item?', element: <WhereUsed /> },
      { path: 'compare/:bomRef?', element: <Compare /> },
      { path: 'changes', element: <ChangePreview /> },
      { path: 'reports', element: <Reports /> },
      { path: '*', element: <NotFound /> },
    ],
  },
])

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}
