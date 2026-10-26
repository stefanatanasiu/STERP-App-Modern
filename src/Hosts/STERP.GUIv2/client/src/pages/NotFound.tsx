import { Compass } from 'lucide-react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/primitives'
import { EmptyState } from '@/components/ui/surfaces'

export default function NotFoundPage() {
  const navigate = useNavigate()
  return (
    <div className="flex h-full items-center justify-center p-8">
      <div className="glass rounded-3xl p-10">
        <EmptyState icon={<Compass className="size-6" />} title="This page is not part of GUIv2 yet">
          GUIv2 starts with bills of material. The classic GUI keeps every other screen.
        </EmptyState>
        <div className="flex justify-center">
          <Button variant="primary" onClick={() => navigate('/')}>
            Back to the overview
          </Button>
        </div>
      </div>
    </div>
  )
}
