import { Fragment, useEffect, useMemo, useState } from 'react';
import { api } from '../api';
import { ErrorMsg, Loading, Modal, StatusBadge, useMonthBlock } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { mh, monthLabel, parseCell, pct, signed } from '../format';
import { CATEGORY_LABEL, CATEGORY_ORDER, type Category, type Cycle, type Department, type Line, type Proposal, type Settings } from '../types';

interface ReviewData {
  cycle: Cycle;
  department: { code: string; name: string; capacityPerMonth: number; effectiveHeads: number };
  review: { status: string; submittedAt?: string; submittedBy?: string; note?: string };
  canEdit: boolean;
  settings: Settings;
  lines: Line[];
  newAllocations: Proposal[];
}

interface Edit { vals: string[]; reason: string; dirty: boolean }

const asText = (v: number | null | undefined) => (v === null || v === undefined ? '' : String(v));

export default function ReviewPage() {
  const { me, cycle, has } = useApp();
  const [depts] = useLoad<Department[]>('/api/departments');
  const own = me?.departments ?? [];
  const choices = has('CorporatePlanning') ? (depts ?? []).map(d => d.code) : own;
  const [dept, setDept] = useState<string>('');
  useEffect(() => { if (!dept && choices.length) setDept(choices[0]); }, [choices, dept]);
  const [data, error, reload] = useLoad<ReviewData>(cycle && dept ? `/api/cycles/${cycle.id}/review/${dept}` : null);
  if (!choices.length && depts) return <div className="empty">No department is assigned to you. Ask the administrator to add your department scope.</div>;
  return (
    <div>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <div>
          <h1>Discipline Review</h1>
          <p className="lead">Type a proposed value only where you disagree with the planner. A blank month keeps the planner value; 0 removes the work. Every change needs a reason.</p>
        </div>
        {choices.length > 1 && (
          <label className="field">Department
            <select value={dept} onChange={e => setDept(e.target.value)} aria-label="Department">
              {choices.map(c => <option key={c} value={c}>{c} {depts?.find(d => d.code === c)?.name}</option>)}
            </select>
          </label>
        )}
      </div>
      <ErrorMsg error={error} />
      {!data ? <Loading /> : <ReviewGrid key={`${dept}-${data.cycle.id}`} data={data} reload={reload} />}
    </div>
  );
}

function ReviewGrid({ data, reload }: { data: ReviewData; reload: () => void }) {
  const { toast } = useApp();
  const c = data.cycle;
  const n = c.monthCount;
  const [range, blockSel] = useMonthBlock(4);
  const months = Array.from({ length: n }, (_, i) => i).filter(i => !range || (i >= range[0] && i < range[1]));
  const initial = () => Object.fromEntries(
    data.lines.map(l => [l.id, { vals: Array.from({ length: n }, (_, i) => asText(l.proposal?.values[i])), reason: l.proposal?.reason ?? '', dirty: false }]));
  const [edits, setEdits] = useState<Record<number, Edit>>(initial);
  // fresh data from the server (after a save, submit or withdraw) replaces the local edits
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setEdits(initial()), [data]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [newOpen, setNewOpen] = useState<Proposal | 'new' | null>(null);
  const [catLine, setCatLine] = useState<Line | null>(null);
  const editable = data.canEdit;

  const planner = (l: Line, i: number) => l.planning[i] ?? 0;
  const proposedValue = (l: Line, i: number) => {
    const p = parseCell(edits[l.id]?.vals[i] ?? '');
    return p === null || p === 'invalid' ? planner(l, i) : p;
  };
  const changed = (l: Line, i: number) => {
    const p = parseCell(edits[l.id]?.vals[i] ?? '');
    return p !== null && p !== 'invalid' && Math.abs(p - planner(l, i)) >= 0.5;
  };
  const setVal = (id: number, i: number, v: string) => setEdits(e => ({ ...e, [id]: { ...e[id], vals: e[id].vals.map((x, k) => (k === i ? v : x)), dirty: true } }));
  const setVals = (id: number, vals: string[]) => setEdits(e => ({ ...e, [id]: { ...e[id], vals, dirty: true } }));

  // quick actions on one line
  const keepAll = (l: Line) => setVals(l.id, Array(n).fill(''));
  const zeroAll = (l: Line) => setVals(l.id, Array.from({ length: n }, (_, i) => (planner(l, i) > 0 ? '0' : '')));
  const shift = (l: Line, by: number) => {
    const src = Array.from({ length: n }, (_, i) => proposedValue(l, i));
    const out = Array.from({ length: n }, (_, i) => { const j = i - by; return j >= 0 && j < n ? src[j] : 0; });
    setVals(l.id, out.map((v, i) => (Math.abs(v - planner(l, i)) < 0.5 ? '' : String(v))));
  };
  const extendTo = (l: Line, until: number) => {
    const cur = Array.from({ length: n }, (_, i) => proposedValue(l, i));
    let last = -1;
    cur.forEach((v, i) => { if (v > 0) last = i; });
    if (last < 0) return;
    const out = cur.map((v, i) => (i > last && i <= until ? cur[last] : v));
    setVals(l.id, out.map((v, i) => (Math.abs(v - planner(l, i)) < 0.5 ? '' : String(v))));
  };

  const dirty = data.lines.filter(l => edits[l.id]?.dirty);
  const invalid = dirty.some(l => edits[l.id].vals.some(v => parseCell(v) === 'invalid'));
  const saveAll = async () => {
    if (invalid) { setErr('Some month cells are not valid numbers.'); return; }
    setBusy(true); setErr(null);
    try {
      for (const l of dirty) {
        const e = edits[l.id];
        await api.post(`/api/cycles/${c.id}/proposals`, {
          lineId: l.id, deptCode: l.dept, kind: 'Change', reason: e.reason,
          values: e.vals.map((v, i) => ({ monthIndex: i, value: parseCell(v) })).filter(x => x.value !== null),
        });
      }
      toast(`${dirty.length} line${dirty.length === 1 ? '' : 's'} saved as draft`);
      reload();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const drafts = data.lines.filter(l => l.proposal && ['Draft', 'Returned'].includes(l.proposal.status)).length
    + data.newAllocations.filter(p => ['Draft', 'Returned'].includes(p.status)).length
    + data.lines.filter(l => l.categoryRequest && ['Draft', 'Returned'].includes(l.categoryRequest.status)).length;
  const submit = async () => {
    if (dirty.length) { setErr('Save your changes before submitting.'); return; }
    const confirmText = drafts === 0
      ? 'Confirm that your department has no changes to this forecast?'
      : `Submit ${drafts} request${drafts === 1 ? '' : 's'} to Corporate Planning? You cannot edit them afterwards unless they are returned.`;
    if (!window.confirm(confirmText)) return;
    setBusy(true); setErr(null);
    try {
      const r = await api.post<{ status: string }>(`/api/cycles/${c.id}/review/${data.department.code}/submit`);
      toast(r.status === 'NoChangeConfirmed' ? 'No-change confirmation recorded' : 'Review submitted to Corporate Planning');
      reload();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const withdraw = async (p: Proposal) => {
    if (!window.confirm('Withdraw this request?')) return;
    try { await api.del(`/api/cycles/${c.id}/proposals/${p.id}`); reload(); } catch (e) { setErr((e as Error).message); }
  };

  // capacity strip: planner vs proposed load against the department's capacity, month by month
  const totals = useMemo(() => Array.from({ length: n }, (_, i) => ({
    planner: data.lines.reduce((s, l) => s + planner(l, i), 0),
    proposed: data.lines.reduce((s, l) => s + proposedValue(l, i), 0)
      + data.newAllocations.filter(p => p.status !== 'Rejected').reduce((s, p) => s + (p.values[i] ?? 0), 0),
  // eslint-disable-next-line react-hooks/exhaustive-deps
  })), [edits, data]);
  const cap = data.department.capacityPerMonth;
  const groups = CATEGORY_ORDER.map(cat => ({ cat, lines: data.lines.filter(l => l.category === cat) })).filter(g => g.lines.length);
  const reqChanged = data.lines.filter(l => l.proposal).length + data.newAllocations.length;

  return (
    <div>
      <div className="card" style={{ marginBottom: 16 }}>
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <div>
            <h2 style={{ marginBottom: 4 }}>{data.department.code} {data.department.name}</h2>
            <div className="row small muted">
              <StatusBadge status={data.review.status} />
              <span>Capacity {mh(cap)} MH / month ({data.department.effectiveHeads} effective heads × {data.settings.hoursPerManMonth})</span>
              {c.reviewDue && <span>Due {new Date(c.reviewDue).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })}</span>}
              <span>{reqChanged} request{reqChanged === 1 ? '' : 's'}</span>
            </div>
            {data.review.status === 'Returned' && data.review.note && <div className="msg warn" style={{ marginTop: 8 }}>Returned by Corporate Planning: {data.review.note}</div>}
            {!editable && <div className="msg info" style={{ marginTop: 8 }}>
              {['Submitted', 'NoChangeConfirmed'].includes(data.review.status) ? 'Your review is submitted. Corporate Planning can return it if changes are needed.' : 'The review is not open for editing at this stage.'}
            </div>}
          </div>
          <div className="row">
            {blockSel}
          </div>
        </div>
        {editable && (
          <div className="row" style={{ marginTop: 12 }}>
            <button className="primary" disabled={busy || dirty.length === 0} onClick={saveAll}>Save {dirty.length || ''} draft{dirty.length === 1 ? '' : 's'}</button>
            <button onClick={() => setNewOpen('new')} disabled={busy}>+ Request new allocation</button>
            <span className="spacer" style={{ flex: 1 }} />
            <button className="accent" disabled={busy} onClick={submit}>{drafts === 0 && reqChanged === 0 ? 'Confirm no change' : `Submit review (${drafts})`}</button>
          </div>
        )}
        <ErrorMsg error={err} />
      </div>

      <div className="table-wrap grid" style={{ maxHeight: '62vh' }}>
        <table>
          <thead>
            <tr>
              <th className="sticky1">Project</th><th className="sticky2">Activity / reason</th>
              {months.map(i => <th key={i} className="num">{monthLabel(c.firstMonth, i)}</th>)}
              <th className="num">Total</th><th>Request</th>
            </tr>
          </thead>
          <tbody>
            {groups.map(g => (
              <Fragment key={g.cat}>
                <tr className="group"><td className="sticky1">{CATEGORY_LABEL[g.cat]}</td><td className="sticky2">{g.lines.length} lines{g.lines[0].corporateControlled ? ' · Corporate Planning controlled (read-only)' : ''}</td>
                  <td colSpan={months.length + 2} /></tr>
                {g.lines.map(l => {
                  const e = edits[l.id];
                  const lineChanged = Array.from({ length: n }, (_, i) => i).some(i => changed(l, i));
                  const ro = !editable || l.corporateControlled;
                  const plannerTotal = l.planning.reduce<number>((s, v) => s + (v ?? 0), 0);
                  const propTotal = Array.from({ length: n }, (_, i) => proposedValue(l, i)).reduce((s, v) => s + v, 0);
                  return (
                    <Fragment key={l.id}>
                      <tr className="planner">
                        <td className="sticky1" rowSpan={2}><strong>{l.project}</strong><div className="small muted">{l.section}</div></td>
                        <td className="sticky2 small">{l.activity}</td>
                        {months.map(i => <td key={i} className="m">{mh(l.planning[i])}</td>)}
                        <td className="m"><strong>{mh(plannerTotal)}</strong></td>
                        <td rowSpan={2} className="small">
                          {l.proposal && <StatusBadge status={l.proposal.status} />}
                          {l.categoryRequest && <div style={{ marginTop: 4 }}><span className="badge orange">→ {CATEGORY_LABEL[l.categoryRequest.requestedCategory as Category]}</span></div>}
                          {l.proposal?.reviewComment && <div className="muted" style={{ marginTop: 4, maxWidth: 200 }}>CP: {l.proposal.reviewComment}</div>}
                        </td>
                      </tr>
                      <tr className="proposed">
                        <td className="sticky2">
                          {ro ? <span className="small muted">{l.proposal?.reason || (l.corporateControlled ? 'Read-only' : '')}</span> : (
                            <>
                              <input aria-label={`Reason for ${l.project}`} placeholder={lineChanged ? 'Reason (required)' : 'Reason'} value={e.reason}
                                     style={{ width: '100%', borderColor: lineChanged && !e.reason.trim() ? 'var(--discipline)' : undefined }}
                                     onChange={ev => setEdits(x => ({ ...x, [l.id]: { ...x[l.id], reason: ev.target.value, dirty: true } }))} />
                              <div className="row" style={{ gap: 2, marginTop: 2 }}>
                                <button className="link small" onClick={() => keepAll(l)} title="Clear all proposed values">Keep</button>
                                <button className="link small" onClick={() => zeroAll(l)} title="Set every month to zero">Zero</button>
                                <button className="link small" onClick={() => shift(l, -1)} title="Move the profile one month earlier">◀ 1m</button>
                                <button className="link small" onClick={() => shift(l, 1)} title="Move the profile one month later">1m ▶</button>
                                <select className="small" aria-label="Extend to month" value="" style={{ padding: '0 2px', fontSize: 11 }}
                                        onChange={ev => ev.target.value && extendTo(l, Number(ev.target.value))}>
                                  <option value="">Extend to…</option>
                                  {Array.from({ length: n }, (_, i) => <option key={i} value={i}>{monthLabel(c.firstMonth, i)}</option>)}
                                </select>
                                <button className="link small" onClick={() => setCatLine(l)}>Category…</button>
                              </div>
                            </>
                          )}
                        </td>
                        {months.map(i => (
                          <td key={i} className="m">
                            {ro ? (l.proposal?.values[i] ?? '') : (
                              <input aria-label={`${l.project} ${monthLabel(c.firstMonth, i)} proposed`} value={e.vals[i]} inputMode="decimal"
                                     className={parseCell(e.vals[i]) === 'invalid' ? 'invalid' : changed(l, i) ? 'changed' : ''}
                                     onChange={ev => setVal(l.id, i, ev.target.value)} />
                            )}
                          </td>
                        ))}
                        <td className="m" style={{ color: lineChanged ? '#a8431a' : undefined }}><strong>{mh(propTotal)}</strong>
                          {lineChanged && <div className="small">{signed(propTotal - plannerTotal)}</div>}</td>
                      </tr>
                    </Fragment>
                  );
                })}
              </Fragment>
            ))}
            {data.newAllocations.length > 0 && (
              <tr className="group"><td className="sticky1">New allocations</td><td className="sticky2">requested by the department</td><td colSpan={months.length + 2} /></tr>
            )}
            {data.newAllocations.map(p => (
              <tr key={p.id} className="proposed">
                <td className="sticky1"><strong>{p.newProjectCode}</strong><div className="small muted">{p.newCategory && CATEGORY_LABEL[p.newCategory]}</div></td>
                <td className="sticky2 small">{p.newActivity}<div className="muted">{p.reason}</div>
                  {editable && ['Draft', 'Returned'].includes(p.status) && <div className="row" style={{ gap: 4 }}>
                    <button className="link small" onClick={() => setNewOpen(p)}>Edit</button><button className="link small" onClick={() => withdraw(p)}>Withdraw</button></div>}
                </td>
                {months.map(i => <td key={i} className="m" style={{ color: '#a8431a' }}>{mh(p.values[i])}</td>)}
                <td className="m"><strong>{mh(p.values.reduce<number>((s, v) => s + (v ?? 0), 0))}</strong></td>
                <td><StatusBadge status={p.status} /></td>
              </tr>
            ))}
            <tr className="group"><td className="sticky1">Capacity</td><td className="sticky2">Department load against capacity</td><td colSpan={months.length + 2} /></tr>
            <tr className="capacity"><td className="sticky1 small">Planner</td><td className="sticky2 small muted">total planner MH</td>
              {months.map(i => <td key={i} className="m" style={{ color: 'var(--planner)' }}>{mh(totals[i].planner)}</td>)}<td /><td /></tr>
            <tr className="capacity"><td className="sticky1 small">Proposed</td><td className="sticky2 small muted">with your proposals and new allocations</td>
              {months.map(i => <td key={i} className="m" style={{ color: '#a8431a' }}>{mh(totals[i].proposed)}</td>)}<td /><td /></tr>
            <tr className="capacity"><td className="sticky1 small">Utilisation</td><td className="sticky2 small muted">proposed / {mh(cap)} MH capacity</td>
              {months.map(i => <td key={i} className={`m${cap > 0 && totals[i].proposed > cap ? ' over' : ''}`}>{cap > 0 ? pct(totals[i].proposed / cap) : '–'}</td>)}<td /><td /></tr>
          </tbody>
        </table>
      </div>
      {newOpen && <NewAllocation cycle={c} dept={data.department.code} existing={newOpen === 'new' ? null : newOpen}
                                 onClose={() => setNewOpen(null)} onSaved={() => { setNewOpen(null); reload(); }} />}
      {catLine && <CategoryChange cycle={c} line={catLine} onClose={() => setCatLine(null)} onSaved={() => { setCatLine(null); reload(); }} />}
    </div>
  );
}

function NewAllocation({ cycle, dept, existing, onClose, onSaved }: { cycle: Cycle; dept: string; existing: Proposal | null; onClose: () => void; onSaved: () => void }) {
  const [q, setQ] = useState(existing?.newProjectCode ?? '');
  const [found] = useLoad<{ code: string; name: string; client?: string }[]>(q.length >= 2 ? `/api/projects?q=${encodeURIComponent(q)}` : null);
  const [code, setCode] = useState(existing?.newProjectCode ?? '');
  const [activity, setActivity] = useState(existing?.newActivity ?? '');
  const [category, setCategory] = useState<Category>(existing?.newCategory ?? 'Proposal');
  const [reason, setReason] = useState(existing?.reason ?? '');
  const [vals, setVals] = useState<string[]>(() => Array.from({ length: cycle.monthCount }, (_, i) => asText(existing?.values[i])));
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    try {
      await api.post(`/api/cycles/${cycle.id}/proposals`, {
        proposalId: existing?.id, deptCode: dept, kind: 'NewAllocation', newProjectCode: code, newActivity: activity, newCategory: category,
        reason, values: vals.map((v, i) => ({ monthIndex: i, value: parseCell(v) })).filter(x => typeof x.value === 'number'),
      });
      onSaved();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Modal title="Request a new allocation" onClose={onClose}>
      <div className="stack">
        <p className="small muted" style={{ margin: 0 }}>Pick the project from the ENPPI project master. A code that is not in the master is recorded as a request for Corporate Planning to confirm.</p>
        <label className="field">Project code or name
          <input value={q} onChange={e => { setQ(e.target.value); setCode(e.target.value.trim()); }} placeholder="e.g. 5248210 or WEST to EAST" />
        </label>
        {found && found.length > 0 && q !== code + ' ' && (
          <div className="table-wrap" style={{ maxHeight: 140 }}>
            <table><tbody>{found.map(p => (
              <tr key={p.code} style={{ cursor: 'pointer' }} onClick={() => { setCode(p.code); setQ(p.code); setActivity(`${p.code} ${p.name}`); }}>
                <td><strong>{p.code}</strong></td><td>{p.name}</td><td className="muted">{p.client}</td></tr>))}</tbody></table>
          </div>
        )}
        <div className="row">
          <label className="field" style={{ flex: 1 }}>Activity / title<input value={activity} onChange={e => setActivity(e.target.value)} /></label>
          <label className="field">Category
            <select value={category} onChange={e => setCategory(e.target.value as Category)}>
              {(['Running', 'UnderSignature', 'Proposal', 'Potential'] as Category[]).map(c => <option key={c} value={c}>{CATEGORY_LABEL[c]}</option>)}
            </select>
          </label>
        </div>
        <div className="table-wrap grid">
          <table><thead><tr>{vals.map((_, i) => <th key={i} className="num">{monthLabel(cycle.firstMonth, i)}</th>)}</tr></thead>
            <tbody><tr className="proposed">{vals.map((v, i) => (
              <td key={i} className="m"><input value={v} aria-label={`${monthLabel(cycle.firstMonth, i)} MH`} onChange={e => setVals(x => x.map((y, k) => (k === i ? e.target.value : y)))} /></td>
            ))}</tr></tbody></table>
        </div>
        <label className="field">Reason (required)<textarea rows={3} value={reason} onChange={e => setReason(e.target.value)} /></label>
        <ErrorMsg error={err} />
        <div className="row end"><button onClick={onClose}>Cancel</button><button className="primary" onClick={save} disabled={!code}>Save draft</button></div>
      </div>
    </Modal>
  );
}

function CategoryChange({ cycle, line, onClose, onSaved }: { cycle: Cycle; line: Line; onClose: () => void; onSaved: () => void }) {
  const [cat, setCat] = useState<Category>(line.categoryRequest?.requestedCategory ?? (line.category === 'Proposal' ? 'Running' : 'Proposal'));
  const [reason, setReason] = useState(line.categoryRequest?.reason ?? '');
  const [err, setErr] = useState<string | null>(null);
  const save = async () => {
    try {
      await api.post(`/api/cycles/${cycle.id}/proposals`, { lineId: line.id, deptCode: line.dept, kind: 'CategoryChange', requestedCategory: cat, reason, values: [] });
      onSaved();
    } catch (e) { setErr((e as Error).message); }
  };
  return (
    <Modal title={`Move ${line.project} to another category`} onClose={onClose}>
      <div className="stack">
        <p className="small muted" style={{ margin: 0 }}>Categories are locked. A move (for example Proposal to Running after award) is a request that Corporate Planning approves. Loan To and Internal stay Corporate Planning controlled.</p>
        <label className="field">From<input value={CATEGORY_LABEL[line.category]} disabled /></label>
        <label className="field">To
          <select value={cat} onChange={e => setCat(e.target.value as Category)}>
            {(['Running', 'UnderSignature', 'Proposal', 'Potential'] as Category[]).filter(c => c !== line.category).map(c => <option key={c} value={c}>{CATEGORY_LABEL[c]}</option>)}
          </select>
        </label>
        <label className="field">Reason (required)<textarea rows={3} value={reason} onChange={e => setReason(e.target.value)} /></label>
        <ErrorMsg error={err} />
        <div className="row end"><button onClick={onClose}>Cancel</button><button className="primary" onClick={save}>Save request</button></div>
      </div>
    </Modal>
  );
}
