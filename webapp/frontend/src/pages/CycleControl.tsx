import { useState } from 'react';
import { api } from '../api';
import { ErrorMsg, Loading, Modal, StatusBadge } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { dateLabel } from '../format';
import { STAGES, STAGE_LABEL, type Cycle, type CycleStatus } from '../types';

interface BoardRow {
  dept: string; name: string; status: string; submittedAt?: string; submittedBy?: string; lastReminderAt?: string; reminders: number;
  note?: string; due?: string; overdue: boolean; drafts: number; submitted: number; pending: number; managers: string[];
}
interface ImportResult { created: number; updated: number; skipped: number; warnings: string[] }
interface Note { id: number; deptCode: string; recipient: string; subject: string; at: string; sent: boolean; error?: string }
interface Audit { id: number; at: string; user: string; action: string; entity: string; entityKey: string; details?: string }

export default function CycleControlPage() {
  const { cycle, has, reloadCycles, toast } = useApp();
  const cp = has('CorporatePlanning');
  const [creating, setCreating] = useState(false);
  const [board, boardErr, reloadBoard] = useLoad<BoardRow[]>(cycle ? `/api/cycles/${cycle.id}/status-board` : null);
  const [tab, setTab] = useState<'board' | 'import' | 'notes' | 'audit'>('board');
  const [err, setErr] = useState<string | null>(null);
  if (!cycle) return (
    <div>
      <h1>Cycle Control</h1>
      <div className="empty">No forecast cycle yet. {cp && <button className="primary" onClick={() => setCreating(true)}>Create the first cycle</button>}</div>
      {creating && <NewCycle onClose={() => setCreating(false)} onDone={async () => { setCreating(false); await reloadCycles(); }} />}
    </div>
  );
  const idx = STAGES.indexOf(cycle.status);
  const move = async (status: CycleStatus) => {
    const verb = status === 'Locked' ? 'Lock the cycle? Final values are frozen and no further edits are possible.' : `Move ${cycle.name} to "${STAGE_LABEL[status]}"?`;
    if (!window.confirm(verb)) return;
    setErr(null);
    try { await api.post(`/api/cycles/${cycle.id}/status`, { status }); await reloadCycles(); reloadBoard(); toast(`Cycle is now: ${STAGE_LABEL[status]}`); }
    catch (e) { setErr((e as Error).message); }
  };
  const remind = async () => {
    try { const r = await api.post<{ sent: number }>(`/api/cycles/${cycle.id}/reminders`); toast(`${r.sent} reminders sent or recorded`); reloadBoard(); }
    catch (e) { setErr((e as Error).message); }
  };
  const returnDept = async (dept: string) => {
    const comment = window.prompt(`Return ${dept}'s review for changes. Comment to the department:`);
    if (!comment) return;
    try { await api.post(`/api/cycles/${cycle.id}/review/${dept}/return`, { comment }); reloadBoard(); toast(`${dept} returned for changes`); }
    catch (e) { setErr((e as Error).message); }
  };
  const answered = board?.filter(b => ['Submitted', 'NoChangeConfirmed'].includes(b.status)).length ?? 0;
  return (
    <div>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <div><h1>Cycle Control</h1><p className="lead">One board for the 26 departments replaces the "Disciplines Review" sheet and the email chasing.</p></div>
        {cp && <button className="primary" onClick={() => setCreating(true)}>+ New cycle</button>}
      </div>
      <div className="card">
        <div className="stepper" aria-label="Cycle stage">
          {STAGES.map((s, i) => <div key={s} className={`step ${i < idx ? 'done' : i === idx ? 'now' : ''}`}>{i + 1}. {STAGE_LABEL[s]}</div>)}
        </div>
        <div className="row">
          {cp && idx < STAGES.length - 1 && <button className="primary" onClick={() => move(STAGES[idx + 1])}>Move to {STAGE_LABEL[STAGES[idx + 1]]} →</button>}
          {cp && idx > 0 && cycle.status !== 'Locked' && <button onClick={() => move(STAGES[idx - 1])}>← Back to {STAGE_LABEL[STAGES[idx - 1]]}</button>}
          <span className="small muted">Window {dateLabel(cycle.firstMonth)} + {cycle.monthCount} months · created by {cycle.createdBy}</span>
        </div>
        {cp && cycle.status !== 'Locked' && <DueDates cycle={cycle} onSaved={reloadCycles} />}
        <ErrorMsg error={err} />
      </div>
      <div className="seg" style={{ margin: '16px 0 12px' }}>
        {(['board', 'import', 'notes', 'audit'] as const).filter(t => cp || t === 'board').map(t => (
          <button key={t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>
            {{ board: 'Status board', import: 'Import data', notes: 'Reminders', audit: 'Audit trail' }[t]}</button>))}
      </div>
      {tab === 'board' && (
        <div className="card">
          <div className="row" style={{ justifyContent: 'space-between', marginBottom: 8 }}>
            <span><strong>{answered}</strong> of {board?.length ?? 0} departments answered · {board?.filter(b => b.overdue).length ?? 0} overdue</span>
            {cp && cycle.status === 'DisciplineReview' && <button onClick={remind}>Send reminders now</button>}
          </div>
          <ErrorMsg error={boardErr} />
          {!board ? <Loading /> : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Dept</th><th>Department</th><th>Review</th><th>Submitted</th><th className="num">Drafts</th><th className="num">Requests</th>
                  <th className="num">Awaiting decision</th><th>Reminders</th><th>Discipline manager</th><th /></tr></thead>
                <tbody>
                  {board.map(b => (
                    <tr key={b.dept}>
                      <td><strong>{b.dept}</strong></td><td>{b.name}</td>
                      <td><StatusBadge status={b.status} />{b.overdue && <span className="badge bad" style={{ marginLeft: 4 }}>! Overdue</span>}</td>
                      <td className="small">{b.submittedAt ? dateLabel(b.submittedAt) : '–'}</td>
                      <td className="num">{b.drafts || ''}</td><td className="num">{b.submitted || ''}</td><td className="num">{b.pending || ''}</td>
                      <td className="small">{b.reminders ? `${b.reminders} · last ${dateLabel(b.lastReminderAt)}` : '–'}</td>
                      <td className="small">{b.managers.join(', ') || <span className="muted">not assigned</span>}</td>
                      <td>{cp && ['Submitted', 'NoChangeConfirmed'].includes(b.status) && cycle.status !== 'Locked' &&
                        <button className="sm" onClick={() => returnDept(b.dept)}>Return</button>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
      {tab === 'import' && <Imports cycle={cycle} onDone={() => { reloadBoard(); reloadCycles(); }} />}
      {tab === 'notes' && <Notifications cycle={cycle} />}
      {tab === 'audit' && <AuditTrail cycle={cycle} />}
      {creating && <NewCycle onClose={() => setCreating(false)} onDone={async () => { setCreating(false); await reloadCycles(); }} />}
    </div>
  );
}

function DueDates({ cycle, onSaved }: { cycle: Cycle; onSaved: () => void }) {
  const { toast } = useApp();
  const [d, setD] = useState({ plannerDue: cycle.plannerDue ?? '', reviewDue: cycle.reviewDue ?? '', approvalDue: cycle.approvalDue ?? '' });
  const save = async () => {
    try {
      await api.put(`/api/cycles/${cycle.id}/due-dates`, { plannerDue: d.plannerDue || null, reviewDue: d.reviewDue || null, approvalDue: d.approvalDue || null });
      toast('Due dates saved'); onSaved();
    } catch (e) { toast((e as Error).message, 'error'); }
  };
  return (
    <div className="row" style={{ marginTop: 12 }}>
      <label className="field">Planner entry due<input type="date" value={d.plannerDue} onChange={e => setD({ ...d, plannerDue: e.target.value })} /></label>
      <label className="field">Discipline review due<input type="date" value={d.reviewDue} onChange={e => setD({ ...d, reviewDue: e.target.value })} /></label>
      <label className="field">Approval due<input type="date" value={d.approvalDue} onChange={e => setD({ ...d, approvalDue: e.target.value })} /></label>
      <button onClick={save} style={{ alignSelf: 'flex-end' }}>Save dates</button>
    </div>
  );
}

function Imports({ cycle, onDone }: { cycle: Cycle; onDone: () => void }) {
  const [result, setResult] = useState<{ kind: string; r: ImportResult } | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (kind: string, file: File | undefined) => {
    if (!file) return;
    setBusy(true); setErr(null); setResult(null);
    try { const r = await api.upload<ImportResult>(`/api/cycles/${cycle.id}/import/${kind}`, file); setResult({ kind, r }); onDone(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const kinds: [string, string, string][] = [
    ['planning', 'Planning Input', 'The values sent to departments (the "To Departments" extract from Primavera). Re-importing replaces planner values.'],
    ['proposals', 'Department feedback (migration)', 'Legacy -c workbooks converted by the analysis engine: blank keeps the planner value; unmatched rows become new-allocation requests.'],
    ['after', 'After implementation', 'The Primavera re-extract after planners implemented the approved changes; drives the implementation check.'],
  ];
  return (
    <div className="card">
      <p className="small muted" style={{ marginTop: 0 }}>Each import uses the same layout: Dept, SubDept, Category, ProjectCode, Activity, Planner, Reason and one column per month. Download the template for this cycle, or produce the files from the old workbooks with the analysis engine (<code>export_webapp.py</code>).</p>
      <div className="table-wrap">
        <table>
          <thead><tr><th>Data</th><th>What it is</th><th>Template</th><th>Upload (.xlsx or .csv)</th></tr></thead>
          <tbody>
            {kinds.map(([kind, label, help]) => (
              <tr key={kind}>
                <td><strong>{label}</strong></td><td className="wrap small">{help}</td>
                <td><button className="sm" onClick={() => api.download(`/api/cycles/${cycle.id}/templates/${kind}`, `${kind}.xlsx`)}>Download</button></td>
                <td><input type="file" accept=".xlsx,.csv" disabled={busy || cycle.status === 'Locked'} aria-label={`Upload ${label}`}
                           onChange={e => run(kind, e.target.files?.[0])} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {busy && <div className="msg info">Importing…</div>}
      <ErrorMsg error={err} />
      {result && (
        <div className="msg ok" style={{ marginTop: 12 }}>
          {result.kind}: {result.r.created} created, {result.r.updated} updated{result.r.skipped ? `, ${result.r.skipped} unchanged rows skipped` : ''}.
          {result.r.warnings.length > 0 && <ul className="small" style={{ margin: '6px 0 0' }}>{result.r.warnings.slice(0, 20).map(w => <li key={w}>{w}</li>)}</ul>}
        </div>
      )}
    </div>
  );
}

function Notifications({ cycle }: { cycle: Cycle }) {
  const [notes, error] = useLoad<Note[]>(`/api/cycles/${cycle.id}/notifications`);
  if (error) return <ErrorMsg error={error} />;
  if (!notes) return <Loading />;
  return (
    <div className="card">
      <p className="small muted" style={{ marginTop: 0 }}>Reminders go out two days before the review due date and daily once overdue, to each department's discipline manager.</p>
      <div className="table-wrap"><table>
        <thead><tr><th>When</th><th>Dept</th><th>To</th><th>Subject</th><th>Result</th></tr></thead>
        <tbody>{notes.map(n => <tr key={n.id}><td className="small">{new Date(n.at).toLocaleString('en-GB')}</td><td>{n.deptCode}</td><td className="small">{n.recipient}</td>
          <td className="wrap small">{n.subject}</td><td>{n.sent ? <span className="badge good">✓ Sent</span> : <span className="badge neutral" title={n.error}>• {n.error ?? 'Not sent'}</span>}</td></tr>)}
          {notes.length === 0 && <tr><td colSpan={5} className="empty">No reminders yet.</td></tr>}</tbody>
      </table></div>
    </div>
  );
}

function AuditTrail({ cycle }: { cycle: Cycle }) {
  const [rows, error] = useLoad<Audit[]>(`/api/cycles/${cycle.id}/audit`);
  if (error) return <ErrorMsg error={error} />;
  if (!rows) return <Loading />;
  return (
    <div className="card">
      <div className="table-wrap" style={{ maxHeight: 520 }}><table>
        <thead><tr><th>When</th><th>User</th><th>Action</th><th>Item</th><th>Details</th></tr></thead>
        <tbody>{rows.map(a => <tr key={a.id}><td className="small">{new Date(a.at).toLocaleString('en-GB')}</td><td className="small">{a.user}</td>
          <td>{a.action}</td><td className="small">{a.entity} {a.entityKey}</td><td className="wrap small">{a.details}</td></tr>)}</tbody>
      </table></div>
    </div>
  );
}

function NewCycle({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const { setCycleId } = useApp();
  const next = new Date(); next.setUTCMonth(next.getUTCMonth() + 1, 1);
  const [name, setName] = useState('');
  const [first, setFirst] = useState(next.toISOString().slice(0, 7));
  const [months, setMonths] = useState(24);
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    try {
      const c = await api.post<Cycle>('/api/cycles', { name, firstMonth: `${first}-01`, monthCount: months });
      setCycleId(c.id);
      onDone();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Modal title="New forecast cycle" onClose={onClose}>
      <div className="stack">
        <label className="field">Name<input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Q1 2027" /></label>
        <div className="row">
          <label className="field">First month<input type="month" value={first} onChange={e => setFirst(e.target.value)} /></label>
          <label className="field">Months<input type="number" min={1} max={60} value={months} onChange={e => setMonths(Number(e.target.value))} /></label>
        </div>
        <p className="small muted" style={{ margin: 0 }}>The cycle starts in Draft. Import the Planning Input, then open planner entry and discipline review from this page.</p>
        <ErrorMsg error={err} />
        <div className="row end"><button onClick={onClose}>Cancel</button><button className="primary" disabled={!name} onClick={save}>Create</button></div>
      </div>
    </Modal>
  );
}
