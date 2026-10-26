import { useQuery } from '@tanstack/react-query'

export interface HostConfig {
  productName: string
  version: string
  classicGuiUrl: string
  apiConsoleUrl: string
}

/** Anonymous host configuration (/bff/config): links to the classic GUI and the API Console. */
export function useHostConfig() {
  return useQuery({
    queryKey: ['host-config'],
    queryFn: async (): Promise<HostConfig> => {
      const r = await fetch('/bff/config', { cache: 'no-store' })
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      return (await r.json()) as HostConfig
    },
    staleTime: Infinity,
  })
}
