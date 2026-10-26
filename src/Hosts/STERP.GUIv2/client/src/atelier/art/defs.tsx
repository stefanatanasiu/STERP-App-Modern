import type { BikeSpec } from '../spec'

// Shared paint for the atelier: shading gradients for bars and tubes (vertical, so a band reads as a cylinder whichever way it
// points), sheens for discs, the bike's paint from the bill, the mat grid and a few filters.

function mix(hex: string, toward: string, k: number): string {
  const a = parseInt(hex.slice(1), 16)
  const b = parseInt(toward.slice(1), 16)
  const ch = (v: number, s: number) => (v >> s) & 255
  const out = [16, 8, 0].map((s) => Math.round(ch(a, s) + (ch(b, s) - ch(a, s)) * k))
  return `#${out.map((v) => v.toString(16).padStart(2, '0')).join('')}`
}

type Stops = [number, string][]

const BANDS: Record<string, Stops> = {
  alloy: [
    [0, '#41464e'],
    [0.16, '#959ca7'],
    [0.3, '#eef1f4'],
    [0.44, '#c2c8d0'],
    [0.7, '#858d98'],
    [0.9, '#555c66'],
    [1, '#363a41'],
  ],
  black: [
    [0, '#040506'],
    [0.2, '#262a31'],
    [0.31, '#4b515b'],
    [0.46, '#1b1e23'],
    [0.8, '#0c0e11'],
    [1, '#030405'],
  ],
  silver: [
    [0, '#58606c'],
    [0.2, '#d9dee5'],
    [0.32, '#ffffff'],
    [0.5, '#b8c0cb'],
    [0.82, '#6e7783'],
    [1, '#3f4650'],
  ],
  steel: [
    [0, '#3b4048'],
    [0.25, '#a4acb7'],
    [0.38, '#dfe4ea'],
    [0.6, '#8d95a0'],
    [1, '#2f343b'],
  ],
  titanium: [
    [0, '#4a4656'],
    [0.25, '#aaa3bd'],
    [0.38, '#e7e1f2'],
    [0.6, '#8f88a3'],
    [1, '#3d3a48'],
  ],
  rubber: [
    [0, '#050505'],
    [0.3, '#2c2c30'],
    [0.42, '#3a3a3f'],
    [0.6, '#161618'],
    [1, '#040404'],
  ],
  brass: [
    [0, '#5e440f'],
    [0.3, '#dcb655'],
    [0.42, '#fff0b5'],
    [0.6, '#bf9634'],
    [1, '#5a400d'],
  ],
  gold: [
    [0, '#6b4a0a'],
    [0.3, '#f2c94c'],
    [0.42, '#fff3c4'],
    [0.6, '#d4a017'],
    [1, '#5c3f08'],
  ],
  primer: [
    [0, '#4f545b'],
    [0.25, '#9aa0a8'],
    [0.36, '#c4c9cf'],
    [0.6, '#80868e'],
    [1, '#41454b'],
  ],
  heat: [
    [0, '#7c1d06'],
    [0.28, '#f97316'],
    [0.42, '#fde68a'],
    [0.6, '#f97316'],
    [1, '#7c1d06'],
  ],
  cardboard: [
    [0, '#8a6439'],
    [0.3, '#c9a170'],
    [0.5, '#d9b88a'],
    [1, '#a07a4a'],
  ],
  white: [
    [0, '#b9bec6'],
    [0.3, '#ffffff'],
    [0.6, '#e9ecf0'],
    [1, '#a9afb8'],
  ],
  blue: [
    [0, '#0b2a5c'],
    [0.3, '#3b82f6'],
    [0.42, '#93c5fd'],
    [0.6, '#2563eb'],
    [1, '#0a2250'],
  ],
}

function paintStops(c: string, finish: BikeSpec['finish']): Stops {
  const shine = finish === 'gloss' ? 0.5 : finish === 'matt' ? 0.16 : 0.3
  return [
    [0, mix(c, '#000000', 0.65)],
    [0.2, mix(c, '#ffffff', shine * 0.45)],
    [0.31, mix(c, '#ffffff', shine)],
    [0.46, c],
    [0.8, mix(c, '#000000', 0.45)],
    [1, mix(c, '#000000', 0.75)],
  ]
}

export const band = (name: string) => `url(#band-${name})`

export function AtelierDefs({ spec }: { spec: BikeSpec }) {
  const bands: [string, Stops][] = [...Object.entries(BANDS), ['paint', paintStops(spec.paint, spec.finish)]]
  return (
    <defs>
      {bands.map(([name, stops]) => (
        <linearGradient key={name} id={`band-${name}`} x1="0" y1="0" x2="0" y2="1">
          {stops.map(([o, c]) => (
            <stop key={o} offset={o} stopColor={c} />
          ))}
        </linearGradient>
      ))}
      <linearGradient id="band-shade" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#000000" stopOpacity="0.6" />
        <stop offset="0.28" stopColor="#ffffff" stopOpacity="0.26" />
        <stop offset="0.42" stopColor="#ffffff" stopOpacity="0.06" />
        <stop offset="0.75" stopColor="#000000" stopOpacity="0.25" />
        <stop offset="1" stopColor="#000000" stopOpacity="0.62" />
      </linearGradient>
      <linearGradient id="sheen" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stopColor="#8d96a3" />
        <stop offset="0.32" stopColor="#f3f6f9" />
        <stop offset="0.5" stopColor="#9ea7b3" />
        <stop offset="0.72" stopColor="#e3e8ee" />
        <stop offset="1" stopColor="#5f6874" />
      </linearGradient>
      <linearGradient id="sheen-dark" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stopColor="#15171b" />
        <stop offset="0.35" stopColor="#3d434c" />
        <stop offset="0.55" stopColor="#1b1e23" />
        <stop offset="0.8" stopColor="#30353d" />
        <stop offset="1" stopColor="#0b0c0f" />
      </linearGradient>
      <linearGradient id="sheen-paint" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stopColor={mix(spec.paint, '#000000', 0.5)} />
        <stop offset="0.35" stopColor={mix(spec.paint, '#ffffff', 0.22)} />
        <stop offset="0.6" stopColor={spec.paint} />
        <stop offset="1" stopColor={mix(spec.paint, '#000000', 0.6)} />
      </linearGradient>
      <radialGradient id="cap-shine" cx="0.38" cy="0.35" r="0.75">
        <stop offset="0" stopColor="#ffffff" stopOpacity="0.9" />
        <stop offset="0.45" stopColor="#c7ced8" />
        <stop offset="1" stopColor="#4a515b" />
      </radialGradient>
      <radialGradient id="cap-dark" cx="0.38" cy="0.35" r="0.75">
        <stop offset="0" stopColor="#5b616b" />
        <stop offset="0.5" stopColor="#1d2025" />
        <stop offset="1" stopColor="#07080a" />
      </radialGradient>
      <radialGradient id="glow-warm" cx="0.5" cy="0.5" r="0.5">
        <stop offset="0" stopColor="#fff7d6" stopOpacity="1" />
        <stop offset="0.35" stopColor="#ffd27a" stopOpacity="0.75" />
        <stop offset="1" stopColor="#ff9a3c" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="glow-red" cx="0.5" cy="0.5" r="0.5">
        <stop offset="0" stopColor="#ffd1d1" stopOpacity="1" />
        <stop offset="0.35" stopColor="#ff4d4d" stopOpacity="0.8" />
        <stop offset="1" stopColor="#ff1f1f" stopOpacity="0" />
      </radialGradient>
      <radialGradient id="spark" cx="0.5" cy="0.5" r="0.5">
        <stop offset="0" stopColor="#ffffff" />
        <stop offset="0.3" stopColor="#ffe08a" />
        <stop offset="1" stopColor="#ff7a18" stopOpacity="0" />
      </radialGradient>
      <linearGradient id="beam" x1="0" x2="1" y1="0" y2="0">
        <stop offset="0" stopColor="#fff3c4" stopOpacity="0.55" />
        <stop offset="1" stopColor="#fff3c4" stopOpacity="0" />
      </linearGradient>
      <radialGradient id="mat-light" cx="0.5" cy="0.42" r="0.68">
        <stop offset="0" stopColor="#2b3d52" />
        <stop offset="0.55" stopColor="#223246" />
        <stop offset="1" stopColor="#161f2d" />
      </radialGradient>
      <radialGradient id="bay-light" cx="0.5" cy="0.55" r="0.55">
        <stop offset="0" stopColor="#67e8f9" stopOpacity="0.16" />
        <stop offset="0.6" stopColor="#a78bfa" stopOpacity="0.05" />
        <stop offset="1" stopColor="#a78bfa" stopOpacity="0" />
      </radialGradient>
      <pattern id="mat-grid" width="100" height="100" patternUnits="userSpaceOnUse">
        <path d="M100 0H0V100" fill="none" stroke="#bfe3ff" strokeOpacity="0.07" strokeWidth="1.6" />
      </pattern>
      <pattern id="mat-grid-major" width="500" height="500" patternUnits="userSpaceOnUse">
        <path d="M500 0H0V500" fill="none" stroke="#bfe3ff" strokeOpacity="0.12" strokeWidth="3" />
      </pattern>
      <pattern id="blueprint-grid" width="100" height="100" patternUnits="userSpaceOnUse">
        <path d="M100 0H0V100" fill="none" stroke="#bfe3ff" strokeOpacity="0.12" strokeWidth="1.4" />
      </pattern>
      <filter id="shadow" x="-10%" y="-10%" width="120%" height="120%" colorInterpolationFilters="sRGB">
        <feDropShadow dx="10" dy="16" stdDeviation="9" floodColor="#03060b" floodOpacity="0.62" />
      </filter>
      <filter id="glow" x="-30%" y="-30%" width="160%" height="160%">
        <feGaussianBlur stdDeviation="6" result="b" />
        <feMerge>
          <feMergeNode in="b" />
          <feMergeNode in="SourceGraphic" />
        </feMerge>
      </filter>
      <filter id="soft" x="-50%" y="-50%" width="200%" height="200%">
        <feGaussianBlur stdDeviation="18" />
      </filter>
    </defs>
  )
}
