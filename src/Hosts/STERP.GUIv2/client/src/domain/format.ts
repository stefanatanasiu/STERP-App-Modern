// Display formatting of API values (en-GB, as the STERP screens). Formatting only rounds what is shown; the exact API value is
// always available as the tooltip (exact()).

const qty = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 6 })
const qtyShort = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 3 })
const int = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 0 })
const pct = new Intl.NumberFormat('en-GB', { style: 'percent', maximumFractionDigits: 1 })
const dateFmt = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
const dateTimeFmt = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })

const moneyFormats = new Map<string, Intl.NumberFormat>()

export function fmtQty(value: number | null | undefined, short = false): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '–'
  return (short ? qtyShort : qty).format(value)
}

export function fmtInt(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '–'
  return int.format(value)
}

export function fmtMoney(value: number | null | undefined, currency = 'GBP'): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '–'
  let f = moneyFormats.get(currency)
  if (!f) {
    f = new Intl.NumberFormat('en-GB', { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 2 })
    moneyFormats.set(currency, f)
  }
  return f.format(value)
}

/** A unit rate keeps up to six decimals (BC unit costs are scale 6). */
export function fmtRate(value: number | null | undefined, currency = 'GBP'): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '–'
  const f = new Intl.NumberFormat('en-GB', { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 6 })
  return f.format(value)
}

export function fmtPct(fraction: number | null | undefined): string {
  if (fraction === null || fraction === undefined || Number.isNaN(fraction)) return '–'
  return pct.format(fraction)
}

/** API dates are ISO (yyyy-MM-dd); date-times are local without zone. */
export function fmtDate(value: string | null | undefined): string {
  if (!value) return '–'
  const d = new Date(value.length === 10 ? `${value}T00:00:00` : value)
  return Number.isNaN(d.getTime()) ? value : dateFmt.format(d)
}

export function fmtDateTime(value: string | null | undefined): string {
  if (!value) return '–'
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? value : dateTimeFmt.format(d)
}

export function exact(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value)
}

export function todayIso(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

/** yyyy-MM-dd -> yyyyMMdd, the form the legacy KEY=VALUE job arguments take. */
export function compactDate(iso: string): string {
  return iso.replaceAll('-', '')
}

export function initials(name: string): string {
  const parts = name.split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
}

export function titleCase(text: string): string {
  return text.toLowerCase().replace(/(^|[\s-])\w/g, (m) => m.toUpperCase())
}
