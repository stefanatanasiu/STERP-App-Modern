/** The ambient backdrop: slow aurora light, a faint engineering grid and film grain. Pure CSS, behind everything. */
export function Aurora() {
  return (
    <div aria-hidden className="pointer-events-none fixed inset-0 -z-10 overflow-hidden bg-ink-950">
      <div className="absolute -top-[30%] -left-[15%] h-[85vh] w-[70vw] animate-aurora-a rounded-full bg-[radial-gradient(closest-side,rgba(124,58,237,0.32),transparent)] blur-3xl" />
      <div className="absolute top-[10%] -right-[20%] h-[80vh] w-[65vw] animate-aurora-b rounded-full bg-[radial-gradient(closest-side,rgba(6,182,212,0.2),transparent)] blur-3xl" />
      <div className="absolute -bottom-[35%] left-[20%] h-[80vh] w-[70vw] animate-aurora-c rounded-full bg-[radial-gradient(closest-side,rgba(236,72,153,0.14),transparent)] blur-3xl" />
      <div
        className="absolute inset-0 opacity-[0.22]"
        style={{
          backgroundImage:
            'linear-gradient(rgba(255,255,255,0.045) 1px, transparent 1px), linear-gradient(90deg, rgba(255,255,255,0.045) 1px, transparent 1px)',
          backgroundSize: '56px 56px',
          maskImage: 'radial-gradient(ellipse 80% 60% at 50% 0%, black 30%, transparent 75%)',
          WebkitMaskImage: 'radial-gradient(ellipse 80% 60% at 50% 0%, black 30%, transparent 75%)',
        }}
      />
      <div
        className="absolute inset-0 opacity-[0.07] mix-blend-overlay"
        style={{
          backgroundImage:
            "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='3' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E\")",
        }}
      />
    </div>
  )
}
