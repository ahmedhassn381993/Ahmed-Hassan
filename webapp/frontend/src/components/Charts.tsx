import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { mh, signed } from '../format';

export interface Series { name: string; color: string; values: number[]; dashed?: boolean }

/** Width of the chart's container, so the SVG is drawn at 1:1 and its text keeps its size at any screen width. */
function useWidth(): [React.RefObject<HTMLDivElement>, number] {
  const ref = useRef<HTMLDivElement>(null);
  const [w, setW] = useState(760);
  useEffect(() => {
    if (!ref.current) return;
    const ro = new ResizeObserver(([e]) => setW(Math.max(320, Math.round(e.contentRect.width))));
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, []);
  return [ref, w];
}

function niceMax(v: number) {
  if (v <= 0) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  const n = v / p;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * p;
}

const compact = (v: number) => (Math.abs(v) >= 1e6 ? `${(v / 1e6).toFixed(1)}M` : Math.abs(v) >= 1e3 ? `${Math.round(v / 1e3)}k` : `${Math.round(v)}`);

function ChartFrame({ title, legend, children, table }: { title: string; legend?: ReactNode; children: ReactNode; table: ReactNode }) {
  const [asTable, setAsTable] = useState(false);
  return (
    <div>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3 style={{ margin: 0 }}>{title}</h3>
        <button className="link small" onClick={() => setAsTable(t => !t)}>{asTable ? 'Show chart' : 'Show table'}</button>
      </div>
      {asTable ? <div className="table-wrap" style={{ maxHeight: 320, marginTop: 8 }}>{table}</div> : <>{children}{legend}</>}
    </div>
  );
}

/** Lines over months on one MH axis, with a crosshair tooltip and direct labels at the line ends. */
export function LineChart({ title, labels, series, height = 280 }: { title: string; labels: string[]; series: Series[]; height?: number }) {
  const [ref, W] = useWidth();
  const [hover, setHover] = useState<number | null>(null);
  const pad = { l: 52, r: 130, t: 12, b: 28 };
  const max = niceMax(Math.max(1, ...series.flatMap(s => s.values)));
  const n = labels.length;
  const x = (i: number) => pad.l + (n <= 1 ? 0 : (i * (W - pad.l - pad.r)) / (n - 1));
  const y = (v: number) => pad.t + (height - pad.t - pad.b) * (1 - v / max);
  const ticks = [0, 0.25, 0.5, 0.75, 1].map(f => f * max);
  // direct end labels, nudged apart so they never overlap
  const ends = useMemo(() => {
    const items = series.map(s => ({ s, yy: y(s.values[n - 1] ?? 0) })).sort((a, b) => a.yy - b.yy);
    for (let i = 1; i < items.length; i++) if (items[i].yy - items[i - 1].yy < 13) items[i].yy = items[i - 1].yy + 13;
    // keep the labels above the month axis: push the stack back up when it overflows the plot
    const bottom = height - pad.b - 4;
    for (let i = items.length - 1; i >= 0; i--) {
      const limit = i === items.length - 1 ? bottom : items[i + 1].yy - 13;
      if (items[i].yy > limit) items[i].yy = limit;
    }
    return items;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [series, max, n, height, W]);
  const every = Math.max(1, Math.ceil(n / Math.max(2, Math.floor((W - pad.l - pad.r) / 64))));

  const onMove = (e: React.MouseEvent) => {
    const box = ref.current?.getBoundingClientRect();
    if (!box) return;
    const px = ((e.clientX - box.left) / box.width) * W;
    const i = Math.round(((px - pad.l) / (W - pad.l - pad.r)) * (n - 1));
    setHover(i >= 0 && i < n ? i : null);
  };

  const table = (
    <table>
      <thead><tr><th>Month</th>{series.map(s => <th key={s.name} className="num">{s.name}</th>)}</tr></thead>
      <tbody>{labels.map((l, i) => <tr key={l}><td>{l}</td>{series.map(s => <td key={s.name} className="num">{mh(s.values[i])}</td>)}</tr>)}</tbody>
    </table>
  );
  const legend = (
    <div className="legend">
      {series.map(s => <span className="key" key={s.name}><span className={`line${s.dashed ? ' dash' : ''}`} style={{ borderColor: s.color }} />{s.name}</span>)}
    </div>
  );
  return (
    <ChartFrame title={title} legend={legend} table={table}>
      <div className="chart" ref={ref} onMouseMove={onMove} onMouseLeave={() => setHover(null)}>
        <svg width={W} height={height} viewBox={`0 0 ${W} ${height}`} role="img" aria-label={title}>
          {ticks.map(t => (
            <g key={t} className="axis">
              <line className={t === 0 ? 'baseline' : 'grid-line'} x1={pad.l} x2={W - pad.r} y1={y(t)} y2={y(t)} />
              <text x={pad.l - 8} y={y(t) + 4} textAnchor="end">{compact(t)}</text>
            </g>
          ))}
          {labels.map((l, i) => (i % every === 0 || (i === n - 1 && (n - 1) % every >= every / 2)) && (
            <text key={l} className="axis" x={x(i)} y={height - 8} textAnchor="middle" style={{ fontSize: 11, fill: 'var(--text-2)' }}>{l}</text>
          ))}
          {series.map(s => (
            <polyline key={s.name} fill="none" stroke={s.color} strokeWidth={2} strokeDasharray={s.dashed ? '6 4' : undefined}
                      strokeLinejoin="round" strokeLinecap="round" points={s.values.map((v, i) => `${x(i)},${y(v)}`).join(' ')} />
          ))}
          {ends.map(({ s, yy }) => (
            <text key={s.name} x={W - pad.r + 8} y={yy + 4} style={{ fontSize: 11, fill: 'var(--text-2)' }}>{s.name}</text>
          ))}
          {hover !== null && (
            <g>
              <line x1={x(hover)} x2={x(hover)} y1={pad.t} y2={height - pad.b} stroke="var(--border-strong)" strokeWidth={1} />
              {series.map(s => <circle key={s.name} cx={x(hover)} cy={y(s.values[hover] ?? 0)} r={4} fill={s.color} stroke="#fff" strokeWidth={2} />)}
            </g>
          )}
        </svg>
        {hover !== null && (
          <div className="tip" style={{ left: `${(x(hover) / W) * 100}%`, top: 8, transform: x(hover) > W * 0.6 ? 'translateX(-105%)' : 'translateX(8px)' }}>
            <div className="t">{labels[hover]}</div>
            {series.map(s => (
              <div key={s.name} className="row" style={{ gap: 6, justifyContent: 'space-between' }}>
                <span><span className="legend-swatch" style={{ background: s.color }} />{s.name}</span><strong>{mh(s.values[hover])}</strong>
              </div>
            ))}
          </div>
        )}
      </div>
    </ChartFrame>
  );
}

/** Horizontal bars about zero: orange = increase, blue = reduction. */
export function DivergingBars({ title, rows }: { title: string; rows: { label: string; value: number; note?: string }[] }) {
  const [ref, W] = useWidth();
  const [hover, setHover] = useState<number | null>(null);
  const rowH = 22, pad = { l: Math.min(260, W * 0.4), r: 70, t: 4, b: 4 };
  const height = pad.t + pad.b + rows.length * rowH;
  const lim = Math.max(1, ...rows.map(r => Math.abs(r.value)));
  const mid = pad.l + (W - pad.l - pad.r) / 2;
  const scale = (W - pad.l - pad.r) / 2 / lim;
  const table = (
    <table>
      <thead><tr><th>Department</th><th className="num">Net MH</th><th>Note</th></tr></thead>
      <tbody>{rows.map(r => <tr key={r.label}><td>{r.label}</td><td className="num">{signed(r.value)}</td><td>{r.note}</td></tr>)}</tbody>
    </table>
  );
  return (
    <ChartFrame title={title} table={table}
      legend={<div className="legend"><span className="key"><span className="legend-swatch" style={{ background: 'var(--discipline)' }} />Increase requested</span>
        <span className="key"><span className="legend-swatch" style={{ background: 'var(--planner)' }} />Reduction requested</span></div>}>
      <div className="chart" ref={ref}>
        <svg width={W} height={height} viewBox={`0 0 ${W} ${height}`} role="img" aria-label={title}>
          <line className="baseline" x1={mid} x2={mid} y1={0} y2={height} />
          {rows.map((r, i) => {
            const w = Math.abs(r.value) * scale, yy = pad.t + i * rowH;
            const xx = r.value >= 0 ? mid : mid - w;
            return (
              <g key={r.label} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
                <rect x={0} y={yy} width={W} height={rowH} fill={hover === i ? '#f3f5f8' : 'transparent'} />
                <text x={pad.l - 8} y={yy + 15} textAnchor="end" style={{ fontSize: 11, fill: 'var(--text)' }}>{r.label}</text>
                {w > 0 && <rect x={xx} y={yy + 4} width={Math.max(1, w)} height={rowH - 8} rx={3}
                                fill={r.value >= 0 ? 'var(--discipline)' : 'var(--planner)'} />}
                <text x={r.value >= 0 ? mid + w + 6 : mid - w - 6} y={yy + 15} textAnchor={r.value >= 0 ? 'start' : 'end'}
                      style={{ fontSize: 11, fill: 'var(--text-2)' }}>{signed(r.value)}</text>
              </g>
            );
          })}
        </svg>
      </div>
    </ChartFrame>
  );
}

/** Bridge of the changes between two totals, drawn from zero (never a truncated axis): one floating bar per step and
 *  a final bar for the net change. */
export function Waterfall({ title, steps, totalLabel }: { title: string; steps: { label: string; value: number }[]; totalLabel: string }) {
  const [ref, W] = useWidth();
  const [hover, setHover] = useState<number | null>(null);
  const height = 240, pad = { l: 64, r: 16, t: 22, b: 40 };
  const bars: { label: string; from: number; to: number; kind: 'total' | 'up' | 'down' }[] = [];
  let run = 0;
  for (const s of steps) { bars.push({ label: s.label, from: run, to: run + s.value, kind: s.value >= 0 ? 'up' : 'down' }); run += s.value; }
  bars.push({ label: totalLabel, from: 0, to: run, kind: 'total' });
  const lo = Math.min(0, ...bars.map(b => Math.min(b.from, b.to)));
  const hi = Math.max(0, ...bars.map(b => Math.max(b.from, b.to)));
  const span = hi - lo || 1;
  const y = (v: number) => pad.t + (height - pad.t - pad.b) * (1 - (v - lo) / span);
  const bw = (W - pad.l - pad.r) / bars.length;
  const color = (k: string) => (k === 'total' ? 'var(--navy)' : k === 'up' ? 'var(--discipline)' : 'var(--planner)');
  const table = (
    <table>
      <thead><tr><th>Step</th><th className="num">MH</th></tr></thead>
      <tbody>
        {steps.map(s => <tr key={s.label}><td>{s.label}</td><td className="num">{signed(s.value)}</td></tr>)}
        <tr><td><strong>{totalLabel}</strong></td><td className="num"><strong>{signed(run)}</strong></td></tr>
      </tbody>
    </table>
  );
  return (
    <ChartFrame title={title} table={table}
      legend={<div className="legend"><span className="key"><span className="legend-swatch" style={{ background: 'var(--discipline)' }} />Adds MH</span>
        <span className="key"><span className="legend-swatch" style={{ background: 'var(--planner)' }} />Removes MH</span>
        <span className="key"><span className="legend-swatch" style={{ background: 'var(--navy)' }} />Net change</span></div>}>
      <div className="chart" ref={ref}>
        <svg width={W} height={height} viewBox={`0 0 ${W} ${height}`} role="img" aria-label={title}>
          {[lo, 0, hi].filter((v, i, a) => a.indexOf(v) === i).map(v => (
            <g key={v} className="axis">
              <line className={v === 0 ? 'baseline' : 'grid-line'} x1={pad.l} x2={W - pad.r} y1={y(v)} y2={y(v)} />
              <text x={pad.l - 8} y={y(v) + 4} textAnchor="end">{v === 0 ? '0' : signed(v)}</text>
            </g>
          ))}
          {bars.map((b, i) => {
            const top = y(Math.max(b.from, b.to)), bottom = y(Math.min(b.from, b.to));
            const value = b.to - b.from;
            return (
              <g key={b.label} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
                <rect x={pad.l + i * bw + bw * 0.18} y={top} width={bw * 0.64} height={Math.max(2, bottom - top)} rx={3} fill={color(b.kind)}
                      opacity={hover === null || hover === i ? 1 : 0.75} />
                <text x={pad.l + i * bw + bw / 2} y={top - 6} textAnchor="middle" style={{ fontSize: 11, fill: 'var(--text)' }}>{signed(value)}</text>
                <text x={pad.l + i * bw + bw / 2} y={height - pad.b + 16} textAnchor="middle" style={{ fontSize: 11, fill: 'var(--text-2)' }}>{b.label}</text>
              </g>
            );
          })}
        </svg>
      </div>
    </ChartFrame>
  );
}
