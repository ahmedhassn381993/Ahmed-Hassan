const nf = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });

export const mh = (v: number | null | undefined) => (v === null || v === undefined ? '' : nf.format(Math.round(v)));
export const signed = (v: number) => (Math.round(v) === 0 ? '0' : (v > 0 ? '+' : '−') + nf.format(Math.abs(Math.round(v))));
export const pct = (v: number | null | undefined, digits = 0) =>
  v === null || v === undefined || !isFinite(v) ? '–' : `${(v * 100).toFixed(digits)}%`;
export const signedPct = (v: number) => (v > 0 ? '+' : v < 0 ? '−' : '') + `${Math.abs(v * 100).toFixed(1)}%`;

export function monthDate(first: string, i: number): Date {
  const [y, m] = first.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1 + i, 1));
}
const mf = new Intl.DateTimeFormat('en-GB', { month: 'short', year: '2-digit', timeZone: 'UTC' });
export const monthLabel = (first: string, i: number) => mf.format(monthDate(first, i)).replace(' ', '-');
export const dateLabel = (d?: string | null) =>
  d ? new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }).format(new Date(d)) : '–';

/** Parses a typed month value: '' = blank (keep the planner value), otherwise a non-negative number. */
export function parseCell(text: string): number | null | 'invalid' {
  const t = text.trim().replace(/,/g, '');
  if (t === '') return null;
  const n = Number(t);
  return isFinite(n) && n >= 0 ? n : 'invalid';
}
