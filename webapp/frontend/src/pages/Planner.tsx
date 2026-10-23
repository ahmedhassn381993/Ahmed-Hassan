import { useMemo, useState } from 'react';
import { api } from '../api';
import { ErrorMsg, Loading, Modal, StatusBadge, useMonthBlock } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { mh, monthLabel, parseCell } from '../format';
import { CATEGORY_LABEL, type Category, type Cycle, type Department, type Line } from '../types';

interface ImplItem {
  proposalId: number; lineId: number; dept: string; category: string; project: string; activity: string; planner?: string;
  planning: number; final: number; after: number; types: string; status: string; implementedAt?: string; implementedBy?: string;
}

export default function PlannerPage() {
  const { cycle, has } = useApp();
  const [tab, setTab] = useState<'grid' | 'impl'>('grid');
  if (!cycle) return <Loading />;
  return (
    <div>
      <h1>My Projects</h1>
      <p className="lead">Planner values by project and discipline (blue), and the approved changes you need to carry into Primavera.</p>
      <div className="seg" style={{ marginBottom: 12 }}>
        <button className={tab === 'grid' ? 'on' : ''} onClick={() => setTab('grid')}>Planner grid</button>
        <button className={tab === 'impl' ? 'on' : ''} onClick={() => setTab('impl')}>Implementation</button>
      </div>
      {tab === 'grid' ? <PlannerGrid cycle={cycle} all={has('CorporatePlanning')} /> : <Implementation cycle={cycle} />}
    </div>
  );
}

function PlannerGrid({ cycle, all }: { cycle: Cycle; all: boolean }) {
  const { toast, me } = useApp();
  const [lines, error, reload] = useLoad<Line[]>(`/api/cycles/${cycle.id}/lines?mine=${all ? 'false' : 'true'}`);
  const [project, setProject] = useState('');
  const [range, blockSel] = useMonthBlock(4);
  const [edits, setEdits] = useState<Record<number, string[]>>({});
  const [err, setErr] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const projects = useMemo(() => Array.from(new Set((lines ?? []).map(l => l.project))).sort(), [lines]);
  const open = cycle.status === 'Draft' || cycle.status === 'PlannerEntry' || all;
  if (error) return <ErrorMsg error={error} />;
  if (!lines) return <Loading />;
  const shown = lines.filter(l => !project || l.project === project);
  const months = Array.from({ length: cycle.monthCount }, (_, i) => i).filter(i => !range || (i >= range[0] && i < range[1]));
  const canEdit = (l: Line) => open && cycle.status !== 'Locked' && (all || (!l.corporateControlled && (!l.planner || l.planner === me?.upn)));
  const save = async () => {
    setErr(null);
    try {
      for (const [id, vals] of Object.entries(edits)) {
        if (vals.some(v => parseCell(v) === 'invalid')) throw new Error('Some cells are not valid numbers.');
        await api.put(`/api/cycles/${cycle.id}/lines/${id}/planner-values`, { values: vals.map((v, i) => ({ monthIndex: i, value: parseCell(v) })) });
      }
      toast(`${Object.keys(edits).length} line(s) saved`);
      setEdits({});
      reload();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <div>
      <div className="row" style={{ marginBottom: 10 }}>
        <label className="field">Project
          <select value={project} onChange={e => setProject(e.target.value)} aria-label="Project">
            <option value="">All my projects ({projects.length})</option>
            {projects.map(p => <option key={p} value={p}>{p} {lines.find(l => l.project === p)?.activity.replace(p, '').slice(0, 50)}</option>)}
          </select>
        </label>
        {blockSel}
        <span style={{ flex: 1 }} />
        {open && <button onClick={() => setAdding(true)}>+ Add line</button>}
        {open && <button className="primary" disabled={!Object.keys(edits).length} onClick={save}>Save changes</button>}
      </div>
      {!open && <div className="msg info">Planner values are locked after planner entry; changes now come through department proposals and approval.</div>}
      <ErrorMsg error={err} />
      <div className="table-wrap grid" style={{ maxHeight: '64vh' }}>
        <table>
          <thead><tr><th className="sticky1">Project</th><th className="sticky2">Discipline / category</th>
            {months.map(i => <th key={i} className="num">{monthLabel(cycle.firstMonth, i)}</th>)}<th className="num">Total</th><th>Department request</th></tr></thead>
          <tbody>
            {shown.map(l => {
              const vals = edits[l.id] ?? l.planning.map(v => (v === null ? '' : String(v)));
              const editable = canEdit(l);
              return (
                <tr key={l.id} className="planner">
                  <td className="sticky1"><strong>{l.project}</strong></td>
                  <td className="sticky2 small">{l.dept} · {l.categoryLabel}{l.corporateControlled ? ' (CP)' : ''}<div className="muted">{l.activity}</div></td>
                  {months.map(i => (
                    <td key={i} className="m">
                      {editable ? <input className="plan" value={vals[i]} aria-label={`${l.project} ${l.dept} ${monthLabel(cycle.firstMonth, i)}`}
                                         onChange={e => setEdits(x => ({ ...x, [l.id]: vals.map((v, k) => (k === i ? e.target.value : v)) }))} />
                        : mh(l.planning[i])}
                    </td>
                  ))}
                  <td className="m"><strong>{mh(vals.reduce((s, v) => s + (Number(v) || 0), 0))}</strong></td>
                  <td>{l.proposal ? <StatusBadge status={l.proposal.status} /> : <span className="small muted">–</span>}</td>
                </tr>
              );
            })}
            {shown.length === 0 && <tr><td colSpan={months.length + 4} className="empty">No lines assigned to you in this cycle.</td></tr>}
          </tbody>
        </table>
      </div>
      {adding && <AddLine cycle={cycle} onClose={() => setAdding(false)} onSaved={() => { setAdding(false); reload(); }} />}
    </div>
  );
}

function AddLine({ cycle, onClose, onSaved }: { cycle: Cycle; onClose: () => void; onSaved: () => void }) {
  const { has } = useApp();
  const [depts] = useLoad<Department[]>('/api/departments');
  const [dept, setDept] = useState('');
  const [code, setCode] = useState('');
  const [activity, setActivity] = useState('');
  const [category, setCategory] = useState<Category>('Running');
  const [vals, setVals] = useState<string[]>(Array(cycle.monthCount).fill(''));
  const [err, setErr] = useState<string | null>(null);
  const cats: Category[] = has('CorporatePlanning') ? ['Running', 'Internal', 'LoanTo', 'UnderSignature', 'Proposal', 'Potential'] : ['Running', 'UnderSignature', 'Proposal', 'Potential'];
  const save = async () => {
    try {
      await api.post(`/api/cycles/${cycle.id}/lines`, { deptCode: dept, category, projectCode: code, activity,
        values: vals.map((v, i) => ({ monthIndex: i, value: parseCell(v) })).filter(x => typeof x.value === 'number') });
      onSaved();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Modal title="Add a forecast line" onClose={onClose}>
      <div className="stack">
        <div className="row">
          <label className="field">Department<select value={dept} onChange={e => setDept(e.target.value)}><option value="" />{depts?.map(d => <option key={d.code} value={d.code}>{d.code} {d.name}</option>)}</select></label>
          <label className="field">Category<select value={category} onChange={e => setCategory(e.target.value as Category)}>{cats.map(c => <option key={c} value={c}>{CATEGORY_LABEL[c]}</option>)}</select></label>
          <label className="field">Project code (from the master)<input maxLength={20} value={code} onChange={e => setCode(e.target.value)} /></label>
        </div>
        <label className="field">Activity<input maxLength={250} value={activity} onChange={e => setActivity(e.target.value)} /></label>
        <div className="table-wrap grid"><table><thead><tr>{vals.map((_, i) => <th key={i} className="num">{monthLabel(cycle.firstMonth, i)}</th>)}</tr></thead>
          <tbody><tr>{vals.map((v, i) => <td key={i} className="m"><input className="plan" value={v} onChange={e => setVals(x => x.map((y, k) => (k === i ? e.target.value : y)))} /></td>)}</tr></tbody></table></div>
        <ErrorMsg error={err} />
        <div className="row end"><button onClick={onClose}>Cancel</button><button className="primary" disabled={!dept || !code} onClick={save}>Add line</button></div>
      </div>
    </Modal>
  );
}

function Implementation({ cycle }: { cycle: Cycle }) {
  const { toast } = useApp();
  const [items, error, reload] = useLoad<ImplItem[]>(`/api/cycles/${cycle.id}/implementation`);
  if (error) return <ErrorMsg error={error} />;
  if (!items) return <Loading />;
  const mark = async (id: number) => {
    try { await api.post(`/api/proposals/${id}/implemented`); toast('Marked as implemented in Primavera'); reload(); }
    catch (e) { toast((e as Error).message, 'error'); }
  };
  return (
    <div className="card">
      <p className="small muted" style={{ marginTop: 0 }}>
        {cycle.hasAfterData ? 'Status compares the after-implementation extract with the approved values.'
          : 'No after-implementation extract is loaded yet: mark each change once it is in Primavera; Corporate Planning loads the extract to confirm.'}
      </p>
      <div className="table-wrap">
        <table>
          <thead><tr><th>Project</th><th>Dept</th><th>Category</th><th>Change</th><th className="num">Planning MH</th><th className="num">Approved MH</th>
            {cycle.hasAfterData && <th className="num">After MH</th>}<th>Status</th><th /></tr></thead>
          <tbody>
            {items.map(x => (
              <tr key={x.proposalId}>
                <td className="wrap"><strong>{x.project}</strong> <span className="small muted">{x.activity}</span></td><td>{x.dept}</td><td>{x.category}</td>
                <td className="small">{x.types}</td><td className="num">{mh(x.planning)}</td><td className="num" style={{ color: 'var(--after-ink)' }}>{mh(x.final)}</td>
                {cycle.hasAfterData && <td className="num">{mh(x.after)}</td>}
                <td><StatusBadge status={x.status} />{x.implementedBy && <div className="small muted">by {x.implementedBy}</div>}</td>
                <td>{!x.implementedAt && cycle.status !== 'Locked' && <button className="sm good" onClick={() => mark(x.proposalId)}>Mark implemented</button>}</td>
              </tr>
            ))}
            {items.length === 0 && <tr><td colSpan={9} className="empty">No approved changes on your projects yet.</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  );
}
