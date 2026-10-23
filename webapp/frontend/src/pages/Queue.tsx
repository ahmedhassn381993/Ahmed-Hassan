import { useMemo, useState } from 'react';
import { api } from '../api';
import { ErrorMsg, Loading, Severity, StatusBadge } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { mh, monthLabel, parseCell, signed } from '../format';
import { CATEGORY_LABEL, type Category, type Cycle, type QueueItem } from '../types';

export default function QueuePage() {
  const { cycle, has, toast } = useApp();
  const [items, error, reload] = useLoad<QueueItem[]>(cycle ? `/api/cycles/${cycle.id}/queue` : null);
  const [status, setStatus] = useState('Submitted');
  const [severity, setSeverity] = useState('');
  const [dept, setDept] = useState('');
  const [open, setOpen] = useState<QueueItem | null>(null);
  const cp = has('CorporatePlanning');
  const filtered = useMemo(() => (items ?? []).filter(x =>
    (!status || x.proposal.status === status) && (!severity || x.severity === severity) && (!dept || x.dept === dept)), [items, status, severity, dept]);
  if (error) return <ErrorMsg error={error} />;
  if (!items || !cycle) return <Loading />;
  const depts = Array.from(new Set(items.map(x => x.dept))).sort();
  const bulk = async () => {
    if (!window.confirm('Approve every submitted change on projects whose total change is below the bulk thresholds?')) return;
    try { const r = await api.post<{ approved: number }>(`/api/cycles/${cycle.id}/bulk-approve`); toast(`${r.approved} small changes approved`); reload(); }
    catch (e) { toast((e as Error).message, 'error'); }
  };
  const pending = items.filter(x => x.proposal.status === 'Submitted').length;
  return (
    <div>
      <h1>Approval Queue</h1>
      <p className="lead">{pending} requests wait for a decision, highest severity first. Nothing changes the forecast until Corporate Planning approves it.</p>
      <div className="row" style={{ marginBottom: 12 }}>
        <label className="field">Status<select value={status} onChange={e => setStatus(e.target.value)}>
          <option value="">All</option>{['Submitted', 'Approved', 'PartiallyApproved', 'Returned', 'Rejected'].map(s => <option key={s} value={s}>{s}</option>)}</select></label>
        <label className="field">Severity<select value={severity} onChange={e => setSeverity(e.target.value)}>
          <option value="">All</option>{['Critical', 'High', 'Medium', 'Low'].map(s => <option key={s}>{s}</option>)}</select></label>
        <label className="field">Department<select value={dept} onChange={e => setDept(e.target.value)}>
          <option value="">All</option>{depts.map(d => <option key={d}>{d}</option>)}</select></label>
        <span style={{ flex: 1 }} />
        {cp && cycle.status !== 'Locked' && <button onClick={bulk}>Bulk-approve small changes</button>}
      </div>
      <div className="table-wrap">
        <table>
          <thead><tr><th>Severity</th><th>Project</th><th>Dept</th><th>Request</th><th className="num">Gross +</th><th className="num">Gross −</th><th className="num">Net</th>
            <th>Reason</th><th>Status</th><th /></tr></thead>
          <tbody>
            {filtered.map(x => (
              <tr key={x.proposal.id} className={open?.proposal.id === x.proposal.id ? 'selected' : ''}>
                <td><Severity level={x.severity} /><div className="small muted">{x.points} pts</div></td>
                <td className="wrap"><strong>{x.project}</strong> <span className="small">{x.projectName ?? x.activity}</span>
                  <div className="small muted">{x.category}{x.projectDepartments ? ` · raised by ${x.projectDepartments}` : ''}</div></td>
                <td>{x.dept}</td>
                <td className="small wrap">{x.proposal.kind === 'CategoryChange' ? `Move to ${CATEGORY_LABEL[x.proposal.requestedCategory as Category]}` : x.change.types.join('; ')}</td>
                <td className="num">{x.change.grossIncrease ? `+${mh(x.change.grossIncrease)}` : ''}</td>
                <td className="num">{x.change.grossReduction ? mh(x.change.grossReduction) : ''}</td>
                <td className="num"><strong>{signed(x.change.net)}</strong></td>
                <td className="wrap small">{x.proposal.reason}</td>
                <td><StatusBadge status={x.proposal.status} /></td>
                <td><button className="sm" onClick={() => setOpen(x)}>{cp && x.proposal.status === 'Submitted' ? 'Decide' : 'View'}</button></td>
              </tr>
            ))}
            {filtered.length === 0 && <tr><td colSpan={10} className="empty">Nothing matches these filters.</td></tr>}
          </tbody>
        </table>
      </div>
      {open && <Decision cycle={cycle} item={open} canDecide={cp && cycle.status !== 'Locked' && !['Draft', 'Returned'].includes(open.proposal.status)}
                         onClose={() => setOpen(null)}
                         onDone={() => { setOpen(null); reload(); }} />}
    </div>
  );
}

function Decision({ cycle, item, canDecide, onClose, onDone }: { cycle: Cycle; item: QueueItem; canDecide: boolean; onClose: () => void; onDone: () => void }) {
  const { toast } = useApp();
  const n = cycle.monthCount;
  const p = item.proposal;
  const move = p.kind === 'CategoryChange';
  const active = Array.from({ length: n }, (_, i) => i).filter(i => item.planning[i] || item.discipline[i] || p.values[i] !== null);
  const [approved, setApproved] = useState<string[]>(() => Array.from({ length: n }, (_, i) =>
    p.approved[i] !== null && p.approved[i] !== undefined ? String(p.approved[i]) : p.values[i] !== null && p.values[i] !== undefined ? String(p.values[i]) : ''));
  const [comment, setComment] = useState(p.reviewComment ?? '');
  const [err, setErr] = useState<string | null>(null);
  const decide = async (action: string) => {
    setErr(null);
    try {
      const body: Record<string, unknown> = { action, comment };
      if (action === 'partial') {
        if (approved.some(v => parseCell(v) === 'invalid')) throw new Error('Approved values must be numbers.');
        body.approvedValues = approved.map((v, i) => ({ monthIndex: i, value: parseCell(v) }));
      }
      await api.post(`/api/proposals/${p.id}/decision`, body);
      toast(`Request ${{ approve: 'approved', partial: 'partly approved', return: 'returned', reject: 'rejected' }[action] ?? action}`);
      onDone();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <div className="overlay" onClick={onClose}>
      <div className="drawer" onClick={e => e.stopPropagation()} role="dialog" aria-label="Decision">
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <h2 style={{ margin: 0 }}>{item.project} · {item.dept}</h2>
          <button className="link" onClick={onClose} aria-label="Close">✕</button>
        </div>
        <p className="muted">{item.activity} · {item.category}</p>
        {move && <div className="card"><h3>Category change</h3>
          <p style={{ margin: 0 }}>Move this row from <strong>{item.category}</strong> to <strong>{CATEGORY_LABEL[p.requestedCategory as Category]}</strong>.
            The man-hours stay as they are; any change to them is a separate request.</p></div>}
        <div className="row" style={{ marginBottom: 12 }}>
          <Severity level={item.severity} /><StatusBadge status={p.status} />
          <span className="small">{item.change.types.join('; ')}</span>
          {item.change.extensionMH > 0 && <span className="badge warn">{mh(item.change.extensionMH)} MH after the planned finish</span>}
        </div>
        <div className="card">
          <h3>Department reason</h3>
          <p style={{ margin: 0 }}>{p.reason || <span className="muted">(none)</span>}</p>
          <div className="small muted" style={{ marginTop: 6 }}>Submitted by {p.createdBy}{p.submittedAt ? ` on ${new Date(p.submittedAt).toLocaleString('en-GB')}` : ''}</div>
        </div>
        {!move && <div className="card">
          <h3>Month by month</h3>
          <div className="table-wrap grid">
            <table>
              <thead><tr><th className="sticky1">Value</th>{active.map(i => <th key={i} className="num">{monthLabel(cycle.firstMonth, i)}</th>)}<th className="num">Total</th></tr></thead>
              <tbody>
                <tr className="planner"><td className="sticky1">Planner</td>{active.map(i => <td key={i} className="m">{mh(item.planning[i])}</td>)}
                  <td className="m"><strong>{mh(item.planning.reduce((a, b) => a + b, 0))}</strong></td></tr>
                <tr><td className="sticky1" style={{ color: '#a8431a' }}>Proposed</td>{active.map(i => (
                  <td key={i} className="m" style={{ color: '#a8431a', fontWeight: item.change.monthsChanged.includes(i) ? 600 : 400 }}>{mh(item.discipline[i])}</td>))}
                  <td className="m"><strong>{mh(item.discipline.reduce((a, b) => a + b, 0))}</strong></td></tr>
                <tr className="proposed"><td className="sticky1" style={{ color: 'var(--after-ink)' }}>Approve</td>{active.map(i => (
                  <td key={i} className="m">{canDecide ? <input value={approved[i]} aria-label={`Approve ${monthLabel(cycle.firstMonth, i)}`}
                    onChange={e => setApproved(a => a.map((v, k) => (k === i ? e.target.value : v)))} /> : mh(item.final[i])}</td>))}<td className="m" /></tr>
              </tbody>
            </table>
          </div>
          <p className="small muted">A blank approve cell keeps the planner value. Edit the figures to approve part of the request.</p>
        </div>}
        {canDecide ? (
          <div className="card">
            <label className="field">Comment to the department (required to return or reject)<textarea rows={3} maxLength={2000} value={comment} onChange={e => setComment(e.target.value)} /></label>
            <ErrorMsg error={err} />
            <div className="row" style={{ marginTop: 10 }}>
              <button className="good" onClick={() => decide('approve')}>Approve as requested</button>
              {!move && <button className="primary" onClick={() => decide('partial')}>Approve edited values</button>}
              <button onClick={() => decide('return')}>Return for revision</button>
              <button className="danger" onClick={() => decide('reject')}>Reject</button>
            </div>
          </div>
        ) : <>
          {p.status === 'Returned' && <p className="small muted">Returned to the department; it can be decided again once the department resubmits it.</p>}
          {p.reviewComment && <div className="card"><h3>Decision</h3><p>{p.reviewComment}</p><div className="small muted">{p.reviewedBy}</div></div>}
        </>}
      </div>
    </div>
  );
}
