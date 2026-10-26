import { create } from 'zustand'

// The build timeline: progress 0 is everything laid out flat on the mat, 1 the assembled bike. Playback runs at a speed
// multiple of the model's natural length (beats); scrubbing pauses.

export const SECONDS_PER_BEAT = 0.42

interface TimelineState {
  progress: number
  playing: boolean
  speed: number
  scrub: (p: number) => void
  play: () => void
  pause: () => void
  restart: () => void
  setSpeed: (s: number) => void
  /** advance during playback by dt seconds of a timeline lasting `seconds` at speed 1 */
  tick: (dt: number, seconds: number) => void
}

export const useAtelierTimeline = create<TimelineState>((set, get) => ({
  progress: 1,
  playing: false,
  speed: 1,
  scrub: (p) => set({ progress: Math.min(1, Math.max(0, p)), playing: false }),
  play: () => set((s) => ({ playing: true, progress: s.progress >= 1 ? 0 : s.progress })),
  pause: () => set({ playing: false }),
  restart: () => set({ progress: 0, playing: true }),
  setSpeed: (speed) => set({ speed }),
  tick: (dt, seconds) => {
    const s = get()
    if (!s.playing) return
    const progress = Math.min(1, s.progress + (dt * s.speed) / Math.max(1, seconds))
    set({ progress, playing: progress < 1 })
  },
}))
