import * as THREE from 'three'

// Procedural textures for the studio (no image files): sponge crumb, marble, candle stripes and kraft paper.

function canvas(size: number, draw: (g: CanvasRenderingContext2D, size: number) => void): THREE.CanvasTexture {
  const c = document.createElement('canvas')
  c.width = c.height = size
  const g = c.getContext('2d')!
  draw(g, size)
  const t = new THREE.CanvasTexture(c)
  t.colorSpace = THREE.SRGBColorSpace
  t.wrapS = t.wrapT = THREE.RepeatWrapping
  t.anisotropy = 8
  return t
}

let seed = 7
function rand() {
  seed = (seed * 16807) % 2147483647
  return (seed - 1) / 2147483646
}

const cache = new Map<string, THREE.Texture>()

function cached(key: string, make: () => THREE.Texture): THREE.Texture {
  let t = cache.get(key)
  if (!t) {
    t = make()
    cache.set(key, t)
  }
  return t
}

/** Open crumb of a vanilla sponge: warm base, airy pores, a few darker specks. */
export function crumbTexture(): THREE.Texture {
  return cached('crumb', () =>
    canvas(512, (g, s) => {
      g.fillStyle = '#e6b860'
      g.fillRect(0, 0, s, s)
      for (let i = 0; i < 2600; i++) {
        const x = rand() * s
        const y = rand() * s
        const r = 0.6 + rand() * 3.2
        g.fillStyle = `rgba(${170 + rand() * 40},${120 + rand() * 30},${50 + rand() * 20},${0.18 + rand() * 0.35})`
        g.beginPath()
        g.ellipse(x, y, r * (1 + rand()), r, rand() * Math.PI, 0, Math.PI * 2)
        g.fill()
      }
      for (let i = 0; i < 900; i++) {
        g.fillStyle = `rgba(255,240,200,${0.25 + rand() * 0.35})`
        g.beginPath()
        g.arc(rand() * s, rand() * s, 0.5 + rand() * 1.6, 0, Math.PI * 2)
        g.fill()
      }
    }),
  )
}

/** Golden baked crust for the outside of a sponge layer. */
export function crustTexture(): THREE.Texture {
  return cached('crust', () =>
    canvas(256, (g, s) => {
      const grad = g.createLinearGradient(0, 0, 0, s)
      grad.addColorStop(0, '#c98a3d')
      grad.addColorStop(1, '#b0742f')
      g.fillStyle = grad
      g.fillRect(0, 0, s, s)
      for (let i = 0; i < 1400; i++) {
        g.fillStyle = `rgba(${90 + rand() * 60},${50 + rand() * 30},${20},${0.12 + rand() * 0.2})`
        g.fillRect(rand() * s, rand() * s, 1 + rand() * 2, 1 + rand() * 2)
      }
    }),
  )
}

/** Nero Marquina marble for the patisserie table: near-black stone with white and gold veins. */
export function marbleTexture(): THREE.Texture {
  return cached('marble', () =>
    canvas(1024, (g, s) => {
      const base = g.createRadialGradient(s / 2, s / 2, s * 0.1, s / 2, s / 2, s * 0.75)
      base.addColorStop(0, '#1f1c22')
      base.addColorStop(1, '#141217')
      g.fillStyle = base
      g.fillRect(0, 0, s, s)
      const veins = (count: number, width: number, alpha: number, color: string) => {
        for (let v = 0; v < count; v++) {
          let x = rand() * s
          let y = rand() * s
          let a = rand() * Math.PI * 2
          g.strokeStyle = color.replace('A', String(alpha * (0.4 + rand() * 0.6)))
          g.lineWidth = width * (0.4 + rand())
          g.beginPath()
          g.moveTo(x, y)
          for (let k = 0; k < 120; k++) {
            a += (rand() - 0.5) * 0.5
            x += Math.cos(a) * 9
            y += Math.sin(a) * 9
            g.lineTo(x, y)
          }
          g.stroke()
        }
      }
      veins(70, 0.5, 0.18, 'rgba(200,195,205,A)')
      veins(24, 1.3, 0.42, 'rgba(235,232,240,A)')
      veins(9, 2.2, 0.32, 'rgba(214,168,78,A)')
    }),
  )
}

/** Spiral candy stripes for the birthday candles. */
export function candleTexture(a: string, b: string): THREE.Texture {
  return cached(`candle-${a}-${b}`, () => {
    const t = canvas(128, (g, s) => {
      g.fillStyle = a
      g.fillRect(0, 0, s, s)
      g.strokeStyle = b
      g.lineWidth = s / 7
      for (let k = -s; k < s * 2; k += s / 3) {
        g.beginPath()
        g.moveTo(k, 0)
        g.lineTo(k + s, s)
        g.stroke()
      }
    })
    t.repeat.set(1, 3)
    return t
  })
}

/** Kraft paper for sacks and the cake box card. */
export function paperTexture(base: string): THREE.Texture {
  return cached(`paper-${base}`, () =>
    canvas(256, (g, s) => {
      g.fillStyle = base
      g.fillRect(0, 0, s, s)
      for (let i = 0; i < 2200; i++) {
        g.fillStyle = `rgba(0,0,0,${rand() * 0.05})`
        g.fillRect(rand() * s, rand() * s, 1, 1 + rand() * 6)
      }
    }),
  )
}

/** A printed label band: name and quantity on a sack, jar or tin. */
export function labelTexture(title: string, accent: string): THREE.Texture {
  return cached(`label-${title}-${accent}`, () =>
    canvas(512, (g, s) => {
      g.fillStyle = '#fbf7ef'
      g.fillRect(0, 0, s, s)
      g.fillStyle = accent
      g.fillRect(0, 0, s, s * 0.16)
      g.fillRect(0, s * 0.84, s, s * 0.16)
      g.fillStyle = '#2b2118'
      g.font = `600 ${Math.round(s * 0.13)}px "Fraunces Variable", Georgia, serif`
      g.textAlign = 'center'
      g.textBaseline = 'middle'
      const words = title.split(' ')
      const lines = words.length > 2 ? [words.slice(0, Math.ceil(words.length / 2)).join(' '), words.slice(Math.ceil(words.length / 2)).join(' ')] : [title]
      lines.forEach((line, i) => g.fillText(line, s / 2, s / 2 + (i - (lines.length - 1) / 2) * s * 0.15, s * 0.9))
    }),
  )
}
