import { useEffect, useState } from 'react';
import { api } from '../api';
import { ErrorMsg, Loading, Modal } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { mh } from '../format';
import type { Department, Settings } from '../types';

interface User { upn: string; displayName: string; role: string; deptCodes?: string | null; email?: string | null; isActive: boolean }
const ROLES = ['CorporatePlanning', 'Planner', 'DisciplineManager', 'Viewer', 'Admin'];

export default function AdminPage() {
  const { has } = useApp();
  const [tab, setTab] = useState<'users' | 'depts' | 'settings' | 'ref'>(has('Admin') ? 'users' : 'depts');
  return (
    <div>
      <h1>Administration</h1>
      <p className="lead">Who can do what, department headcount (capacity), thresholds, and the project and department masters.</p>
      <div className="seg" style={{ marginBottom: 12 }}>
        {has('Admin') && <button className={tab === 'users' ? 'on' : ''} onClick={() => setTab('users')}>Users and scope</button>}
        <button className={tab === 'depts' ? 'on' : ''} onClick={() => setTab('depts')}>Departments and capacity</button>
        {has('Admin') && <button className={tab === 'settings' ? 'on' : ''} onClick={() => setTab('settings')}>Settings</button>}
        <button className={tab === 'ref' ? 'on' : ''} onClick={() => setTab('ref')}>Reference data import</button>
      </div>
      {tab === 'users' && <Users />}
      {tab === 'depts' && <Departments />}
      {tab === 'settings' && <SettingsForm />}
      {tab === 'ref' && <Reference />}
    </div>
  );
}

function Users() {
  const [users, error, reload] = useLoad<User[]>('/api/admin/users');
  const [edit, setEdit] = useState<User | null>(null);
  if (error) return <ErrorMsg error={error} />;
  if (!users) return <Loading />;
  return (
    <div className="card">
      <p className="small muted" style={{ marginTop: 0 }}>In production the role comes from the Entra ID app role (or group) assignment; this table adds the department scope of each discipline manager and covers users without an app role.</p>
      <div className="row" style={{ marginBottom: 8 }}><button className="primary" onClick={() => setEdit({ upn: '', displayName: '', role: 'DisciplineManager', isActive: true })}>+ Add user</button></div>
      <div className="table-wrap"><table>
        <thead><tr><th>User</th><th>Name</th><th>Role</th><th>Departments</th><th>Email</th><th>Active</th><th /></tr></thead>
        <tbody>{users.map(u => <tr key={u.upn}><td>{u.upn}</td><td>{u.displayName}</td><td>{u.role}</td><td>{u.deptCodes}</td><td className="small">{u.email}</td>
          <td>{u.isActive ? 'Yes' : 'No'}</td><td><button className="sm" onClick={() => setEdit(u)}>Edit</button></td></tr>)}</tbody>
      </table></div>
      {edit && <UserForm user={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); reload(); }} />}
    </div>
  );
}

function UserForm({ user, onClose, onSaved }: { user: User; onClose: () => void; onSaved: () => void }) {
  const [u, setU] = useState(user);
  const [err, setErr] = useState<string | null>(null);
  const save = async () => { try { await api.put('/api/admin/users', u); onSaved(); } catch (e) { setErr((e as Error).message); } };
  return (
    <Modal title={user.upn ? `Edit ${user.upn}` : 'Add user'} onClose={onClose}>
      <div className="stack">
        <label className="field">Sign-in name (UPN)<input value={u.upn} disabled={!!user.upn} onChange={e => setU({ ...u, upn: e.target.value })} /></label>
        <label className="field">Display name<input value={u.displayName} onChange={e => setU({ ...u, displayName: e.target.value })} /></label>
        <div className="row">
          <label className="field">Role<select value={u.role} onChange={e => setU({ ...u, role: e.target.value })}>{ROLES.map(r => <option key={r}>{r}</option>)}</select></label>
          <label className="field" style={{ flex: 1 }}>Departments (comma-separated, e.g. 2410,2420)<input value={u.deptCodes ?? ''} onChange={e => setU({ ...u, deptCodes: e.target.value })} /></label>
        </div>
        <label className="field">Email for reminders<input value={u.email ?? ''} onChange={e => setU({ ...u, email: e.target.value })} /></label>
        <label className="row small"><input type="checkbox" checked={u.isActive} onChange={e => setU({ ...u, isActive: e.target.checked })} /> Active</label>
        <ErrorMsg error={err} />
        <div className="row end"><button onClick={onClose}>Cancel</button><button className="primary" onClick={save} disabled={!u.upn}>Save</button></div>
      </div>
    </Modal>
  );
}

function Departments() {
  const { toast } = useApp();
  const [rows, error, reload] = useLoad<Department[]>('/api/departments');
  const [edits, setEdits] = useState<Record<string, Department>>({});
  if (error) return <ErrorMsg error={error} />;
  if (!rows) return <Loading />;
  const set = (d: Department, k: keyof Department, v: number) => setEdits(e => ({ ...e, [d.code]: { ...(e[d.code] ?? d), [k]: v } }));
  const save = async () => {
    try {
      for (const d of Object.values(edits))
        await api.put(`/api/admin/departments/${d.code}`, { name: d.name, parentCode: d.parentCode, permanent: d.permanent, temporary: d.temporary,
          managers: d.managers, loanToUnpaid: d.loanToUnpaid, isActive: d.isActive });
      toast('Headcount saved'); setEdits({}); reload();
    } catch (e) { toast((e as Error).message, 'error'); }
  };
  return (
    <div className="card">
      <p className="small muted" style={{ marginTop: 0 }}>Capacity = (ROUNDUP(permanent × effectiveness) + temporary) × MH per man-month. Managers and loan-to / unpaid staff are excluded.</p>
      <div className="row" style={{ marginBottom: 8 }}><button className="primary" disabled={!Object.keys(edits).length} onClick={save}>Save headcount</button></div>
      <div className="table-wrap"><table>
        <thead><tr><th>Dept</th><th>Name</th><th>Parent</th><th className="num">Permanent</th><th className="num">Temporary</th><th className="num">Managers</th>
          <th className="num">Loan-to / unpaid</th><th className="num">Effective heads</th><th className="num">Capacity MH / month</th></tr></thead>
        <tbody>{rows.map(d0 => {
          const d = edits[d0.code] ?? d0;
          const num = (k: keyof Department) => <input type="number" min={0} style={{ width: 70 }} value={d[k] as number}
                                                      aria-label={`${d.code} ${k}`} onChange={e => set(d0, k, Number(e.target.value))} />;
          return <tr key={d.code}><td><strong>{d.code}</strong></td><td>{d.name}</td><td>{d.parentCode}</td><td className="num">{num('permanent')}</td>
            <td className="num">{num('temporary')}</td><td className="num">{num('managers')}</td><td className="num">{num('loanToUnpaid')}</td>
            <td className="num">{d0.effectiveHeads}</td><td className="num">{mh(d0.capacityPerMonth)}</td></tr>;
        })}</tbody>
      </table></div>
    </div>
  );
}

function SettingsForm() {
  const { toast } = useApp();
  const [s, error] = useLoad<Settings>('/api/settings');
  const [v, setV] = useState<Settings | null>(null);
  useEffect(() => { if (s) setV(s); }, [s]);
  if (error) return <ErrorMsg error={error} />;
  if (!v) return <Loading />;
  const fields: [keyof Settings, string][] = [
    ['hoursPerManMonth', 'MH per man-month'], ['permanentEffectiveness', 'Permanent staff effectiveness (0-1)'],
    ['bulkMaxPct', 'Bulk approval: max change as share of project planning (0-1)'], ['bulkMaxMH', 'Bulk approval: max change per project (MH)'],
    ['justifyPct', 'Fuller justification above this share (0-1)'], ['justifyMH', 'Fuller justification above this change (MH)'],
    ['justifyMinChars', 'Minimum characters for a fuller justification'], ['reminderDaysBefore', 'Reminder days before the due date'],
  ];
  const save = async () => { try { setV(await api.put<Settings>('/api/admin/settings', v)); toast('Settings saved'); } catch (e) { toast((e as Error).message, 'error'); } };
  return (
    <div className="card" style={{ maxWidth: 640 }}>
      <div className="stack">
        {fields.map(([k, label]) => <label key={k} className="field">{label}
          <input type="number" step="any" value={v[k]} onChange={e => setV({ ...v, [k]: Number(e.target.value) })} /></label>)}
        <div className="row end"><button className="primary" onClick={save}>Save settings</button></div>
      </div>
    </div>
  );
}

function Reference() {
  const [msg, setMsg] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const run = async (kind: string, file?: File) => {
    if (!file) return;
    setMsg(null); setErr(null);
    try {
      const r = await api.upload<{ created: number; updated: number }>(`/api/admin/import/${kind}`, file);
      setMsg(`${kind}: ${r.created} created, ${r.updated} updated`);
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <div className="card">
      <div className="table-wrap"><table>
        <thead><tr><th>Master</th><th>Columns</th><th>Upload</th></tr></thead>
        <tbody>
          <tr><td><strong>Project master</strong></td><td className="small">PROJECT_NO (or Code), PROJECT_NAME (or Name), CLIENT_SHORT_NAME, PROJECT_CATEGORY – the PROJECTS export works as it is</td>
            <td><input type="file" accept=".xlsx,.csv" aria-label="Upload project master" onChange={e => run('projects', e.target.files?.[0])} /></td></tr>
          <tr><td><strong>Departments and headcount</strong></td><td className="small">Code, Name, ParentCode, Permanent, Temporary, Managers, LoanToUnpaid</td>
            <td><input type="file" accept=".xlsx,.csv" aria-label="Upload departments" onChange={e => run('departments', e.target.files?.[0])} /></td></tr>
        </tbody>
      </table></div>
      {msg && <div className="msg ok" style={{ marginTop: 12 }}>{msg}</div>}
      <ErrorMsg error={err} />
    </div>
  );
}
