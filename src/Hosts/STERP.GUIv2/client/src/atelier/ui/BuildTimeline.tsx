import { Gauge, Pause, Play, RotateCcw, Wrench } from 'lucide-react'
import { useRef, type KeyboardEvent, type PointerEvent } from 'react'
import { fmtQty } from '@/domain/format'
import { Code, IconButton } from '@/components/ui/primitives'
import { stepIndexAt } from '../evaluate'
import { SECONDS_PER_BEAT, useAtelierTimeline } from '../store'
import type { AtelierModel } from '../types'

const SPEEDS = [0.5, 1, 2]

/** The build timeline: play, pause, restart, speed, and a scrubber from everything laid flat to the assembled bike. */
export function BuildTimeline({ model }: { model: AtelierModel }) {
  const progress = useAtelierTimeline((s) => s.progress)
  const playing = useAtelierTimeline((s) => s.playing)
  const speed = useAtelierTimeline((s) => s.speed)
  const { play, pause, restart, scrub, setSpeed } = useAtelierTimeline.getState()
  const n = model.steps.length
  const index = stepIndexAt(model, progress)
  const done = index >= n
  const step = done ? null : model.steps[index]
  const line = step && step.id !== 'intro' ? step.node.line : null
  const seconds = (model.beats * SECONDS_PER_BEAT) / speed
  const clock = (s: number) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`
  return (
    <div className="glass-strong w-full rounded-2xl px-4 pt-3 pb-2.5">
      <div className="flex items-center gap-2.5">
        <IconButton label={playing ? 'Pause the build' : 'Play the build'} onClick={() => (playing ? pause() : play())}>
          {playing ? <Pause className="size-4" /> : <Play className="size-4" />}
        </IconButton>
        <IconButton label="Build again from the flat lay" onClick={restart}>
          <RotateCcw className="size-4" />
        </IconButton>
        <button
          onClick={() => setSpeed(SPEEDS[(SPEEDS.indexOf(speed) + 1) % SPEEDS.length])}
          title="Playback speed"
          className="flex h-8 cursor-pointer items-center gap-1 rounded-lg px-2 text-[12px] font-semibold text-ink-200 tabular ring-1 ring-white/10 hover:bg-white/[0.06]"
        >
          <Gauge className="size-3.5" />
          {speed}×
        </button>
        <div className="flex min-w-0 flex-1 items-center gap-2.5 pl-1">
          <span className="flex shrink-0 items-center gap-1.5 text-[10.5px] font-semibold tracking-[0.12em] text-cyan-200/90 uppercase">
            <Wrench className="size-3.5" />
            {done ? 'Assembled' : index === 0 ? 'Flat lay' : `Step ${index} of ${n - 1}`}
          </span>
          {step && <span className="size-2 shrink-0 rounded-full" style={{ background: step.color, boxShadow: `0 0 10px ${step.color}` }} />}
          <span className="truncate text-[13px] font-semibold text-ink-50">{done ? model.root.line.description : step?.title}</span>
          {line && (
            <>
              <Code className="shrink-0 text-[11px] text-ink-300">{line.item}</Code>
              <span className="shrink-0 text-[11.5px] text-ink-400 tabular">
                {fmtQty(line.required, true)} {line.unit.trim()}
              </span>
            </>
          )}
          {done && <span className="shrink-0 text-[11.5px] text-ink-400">{n - 1} steps from the explosion</span>}
        </div>
        <span className="shrink-0 text-[11px] text-ink-500 tabular">
          {clock(progress * seconds)} / {clock(seconds)}
        </span>
      </div>
      <Scrubber model={model} progress={progress} onScrub={scrub} />
      <div className="mt-1 flex justify-between text-[10.5px] text-ink-500">
        <span>Everything laid out flat</span>
        <span>Subassemblies, bottom up</span>
        <span>Final assembly</span>
      </div>
    </div>
  )
}

function Scrubber({ model, progress, onScrub }: { model: AtelierModel; progress: number; onScrub: (p: number) => void }) {
  const track = useRef<HTMLDivElement>(null)
  const steps = model.steps
  const at = (e: PointerEvent<HTMLDivElement>) => {
    const r = track.current!.getBoundingClientRect()
    onScrub((e.clientX - r.left) / r.width)
  }
  const keys = (e: KeyboardEvent<HTMLDivElement>) => {
    const i = stepIndexAt(model, progress)
    const back = progress > (steps[i]?.start ?? 0) + 1e-4 ? i : i - 1
    const next =
      e.key === 'ArrowRight' || e.key === 'ArrowUp'
        ? (steps[i + 1]?.start ?? 1)
        : e.key === 'ArrowLeft' || e.key === 'ArrowDown'
          ? (steps[Math.max(0, back)]?.start ?? 0)
          : e.key === 'Home'
            ? 0
            : e.key === 'End'
              ? 1
              : null
    if (next === null) return
    e.preventDefault()
    onScrub(next)
  }
  const finalStart = steps.find((s) => s.id.startsWith('fit-'))?.start ?? 1
  return (
    <div
      ref={track}
      role="slider"
      tabIndex={0}
      aria-label="Build progress"
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(progress * 100)}
      onKeyDown={keys}
      onPointerDown={(e) => {
        e.currentTarget.setPointerCapture(e.pointerId)
        at(e)
      }}
      onPointerMove={(e) => {
        if (e.currentTarget.hasPointerCapture(e.pointerId)) at(e)
      }}
      className="group relative mt-3 h-6 cursor-pointer touch-none outline-none"
    >
      <div className="absolute inset-x-0 top-1/2 h-1.5 -translate-y-1/2 rounded-full bg-white/[0.07] ring-1 ring-white/10 ring-inset" />
      <div className="absolute top-1/2 h-3 -translate-y-1/2 rounded-full bg-pink-300/10 ring-1 ring-pink-200/15" style={{ left: `${finalStart * 100}%`, right: 0 }} />
      <div
        className="absolute top-1/2 left-0 h-1.5 -translate-y-1/2 rounded-full bg-[linear-gradient(90deg,#67e8f9,#a78bfa,#f9a8d4)] shadow-[0_0_14px_rgba(103,232,249,0.55)]"
        style={{ width: `${progress * 100}%` }}
      />
      {steps.slice(1).map((s) => {
        const seated = progress >= s.end
        const big = s.id.startsWith('build-')
        return (
          <span
            key={s.id}
            title={s.title}
            className="absolute top-1/2 rounded-full ring-2 ring-ink-950/80"
            style={{ left: `${s.end * 100}%`, width: big ? 9 : 5, height: big ? 9 : 5, background: seated ? s.color : 'rgba(255,255,255,0.22)', transform: 'translate(-50%,-50%)' }}
          />
        )
      })}
      <span
        className="absolute top-1/2 size-4 -translate-x-1/2 -translate-y-1/2 rounded-full bg-white shadow-[0_0_0_4px_rgba(103,232,249,0.25),0_4px_14px_rgba(0,0,0,0.6)] group-focus-visible:shadow-[0_0_0_5px_rgba(167,139,250,0.6)]"
        style={{ left: `${progress * 100}%` }}
      />
    </div>
  )
}
