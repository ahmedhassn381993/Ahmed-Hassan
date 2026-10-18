import { useMemo, useState } from 'react';
import { api } from '../api';
import { ErrorMsg, Loading, Severity, StatusBadge } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { mh, pct, signed, signedPct } from '../format';
import type { DeptSummary, ProjectSeverity } from '../types';

interface Deviation {
  proposalId?: number; dept: string; section: string; category: string; project: string; activity: string; kind: string; status: string;
  reason: string; planning: number; discipline: number; final: number; after: number; grossIncrease: number; grossReduction: number;
  net: number; types: string; briefCategories: string; monthsChanged: string; requestedCategory?: string; implementation: string;
  reviewedBy?: string; reviewComment?: string;
}

export default function ReportsPage() {
  const { cycle, has, toast } = useApp();
  const [tab, setTab] = useState<'dept' | 'proj' | 'dev'>('dept');
  if (!cycle) return <Loading />;
  const exportAllowed = has('CorporatePlanning', 'Viewer', 'Admin');
  const dl = (kind: string, name: string) => api.download(`/api/cycles/${cycle.id}/export/${kind}`, name).catch(e => toast(e.message, 'error'));
  return (
    <div>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <div><h1>Reports</h1><p className="lead">Planning Input vs Discipline Comment vs Approved / After Implementation, by department, project and request.</p></div>
        {exportAllowed && (
          <div className="row">
            <button onClick={() => dl('excel', 'workload_report.xlsx')}>Excel report</button>
            <button className="primary" onClick={() => dl('powerbi', 'powerbi.zip')} title="CSV files for the Workload Comments Analysis Power BI template">Power BI data (.zip)</button>
          </div>
        )}
      </div>
      <div className="seg" style={{ marginBottom: 12 }}>
        <button className={tab === 'dept' ? 'on' : ''} onClick={() => setTab('dept')}>Departments</button>
        <button className={tab === 'proj' ? 'on' : ''} onClick={() => setTab('proj')}>Projects</button>
        <button className={tab === 'dev' ? 'on' : ''} onClick={() => setTab('dev')}>Deviation register</button>
      </div>
      {tab === 'dept' && <Departments cycleId={cycle.id} />}
      {tab === 'proj' && <Projects cycleId={cycle.id} />}
      {tab === 'dev' && <Deviations cycleId={cycle.id} />}
    </div>
  );
}

function Departments({ cycleId }: { cycleId: number }) {
  const [rows, error] = useLoad<DeptSummary[]>(`/api/cycles/${cycleId}/reports/departments`);
  if (error) return <ErrorMsg error={error} />;
  if (!rows) return <Loading />;
  const sorted = [...rows].sort((a, b) => Math.abs(b.net) - Math.abs(a.net));
  const t = (f: (r: DeptSummary) => number) => rows.reduce((s, r) => s + f(r), 0);
  return (
    <div className="table-wrap">
      <table>
        <thead><tr><th>Dept</th><th>Department</th><th>Review</th><th className="num">Planning MH</th><th className="num">Discipline MH</th><th className="num">Final MH</th>
          <th className="num">Gross +</th><th className="num">Gross −</th><th className="num">Net</th><th className="num">Dev. %</th><th className="num">Requests</th>
          <th className="num">Capacity / month</th><th className="num">Util. (disc.)</th><th className="num">Firm util.</th><th className="num">Overload months</th><th className="num">Implemented</th></tr></thead>
        <tbody>
          {sorted.map(r => (
            <tr key={r.deptCode}>
              <td><strong>{r.deptCode}</strong></td><td>{r.department}</td><td><StatusBadge status={r.reviewStatus} /></td>
              <td className="num">{mh(r.planning)}</td><td className="num">{mh(r.discipline)}</td><td className="num">{mh(r.final)}</td>
              <td className="num">{r.grossIncrease ? `+${mh(r.grossIncrease)}` : ''}</td><td className="num">{r.grossReduction ? mh(r.grossReduction) : ''}</td>
              <td className="num"><strong>{signed(r.net)}</strong></td><td className="num">{r.planning ? signedPct(r.deviationPct) : '–'}</td>
              <td className="num">{r.concernItems || ''}</td><td className="num">{mh(r.capacityPerMonth)}</td>
              <td className="num">{pct(r.utilisationDiscipline)}</td>
              <td className="num" style={r.firmUtilisationDiscipline > 1 ? { color: 'var(--bad)', fontWeight: 600 } : undefined}>{pct(r.firmUtilisationDiscipline)}</td>
              <td className="num">{r.overloadMonths || ''}</td><td className="num">{r.concernItems ? pct(r.implementationRate) : ''}</td>
            </tr>
          ))}
          <tr><td colSpan={3}><strong>Total</strong></td><td className="num"><strong>{mh(t(r => r.planning))}</strong></td><td className="num"><strong>{mh(t(r => r.discipline))}</strong></td>
            <td className="num"><strong>{mh(t(r => r.final))}</strong></td><td className="num">+{mh(t(r => r.grossIncrease))}</td><td className="num">{mh(t(r => r.grossReduction))}</td>
            <td className="num"><strong>{signed(t(r => r.net))}</strong></td><td colSpan={7} /></tr>
        </tbody>
      </table>
    </div>
  );
}

function Projects({ cycleId }: { cycleId: number }) {
  const [rows, error] = useLoad<ProjectSeverity[]>(`/api/cycles/${cycleId}/reports/projects`);
  const [onlyConcern, setOnlyConcern] = useState(true);
  if (error) return <ErrorMsg error={error} />;
  if (!rows) return <Loading />;
  const shown = rows.filter(r => !onlyConcern || r.level !== 'No Concern');
  return (
    <div>
      <label className="row small" style={{ marginBottom: 8 }}><input type="checkbox" checked={onlyConcern} onChange={e => setOnlyConcern(e.target.checked)} /> Projects with a concern only</label>
      <div className="table-wrap">
        <table>
          <thead><tr><th>Project</th><th>Severity</th><th className="num">Points</th><th className="num">Depts</th><th>Raised by</th><th className="num">Planning MH</th>
            <th className="num">Discipline MH</th><th className="num">Gross +</th><th className="num">Gross −</th><th className="num">Net</th><th className="num">Months</th><th>Types</th><th>Master</th></tr></thead>
          <tbody>
            {shown.map(p => (
              <tr key={p.projectCode}>
                <td className="wrap"><strong>{p.projectCode}</strong> {p.name}</td><td><Severity level={p.level} /></td><td className="num">{p.points}</td>
                <td className="num">{p.deptsRaising || ''}</td><td className="small">{p.departments}</td><td className="num">{mh(p.planning)}</td><td className="num">{mh(p.discipline)}</td>
                <td className="num">{p.grossIncrease ? `+${mh(p.grossIncrease)}` : ''}</td><td className="num">{p.grossReduction ? mh(p.grossReduction) : ''}</td>
                <td className="num"><strong>{signed(p.net)}</strong></td><td className="num">{p.monthsAffected || ''}</td><td className="wrap small">{p.types}</td>
                <td>{p.inMaster ? <span className="badge good">✓ In master</span> : <span className="badge warn">? Not in master</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Deviations({ cycleId }: { cycleId: number }) {
  const [rows, error] = useLoad<Deviation[]>(`/api/cycles/${cycleId}/reports/deviations`);
  const [q, setQ] = useState('');
  const shown = useMemo(() => (rows ?? []).filter(r => !q || `${r.dept} ${r.project} ${r.activity} ${r.types} ${r.reason}`.toLowerCase().includes(q.toLowerCase())), [rows, q]);
  if (error) return <ErrorMsg error={error} />;
  if (!rows) return <Loading />;
  return (
    <div>
      <input placeholder="Filter by department, project, type or reason" value={q} onChange={e => setQ(e.target.value)} style={{ width: 360, marginBottom: 8 }} />
      <div className="table-wrap" style={{ maxHeight: '66vh' }}>
        <table>
          <thead><tr><th>Dept</th><th>Project</th><th>Category</th><th>Request</th><th className="num">Planning</th><th className="num">Discipline</th><th className="num">Net</th>
            <th>Months changed</th><th>Reason</th><th>Decision</th><th>Implementation</th></tr></thead>
          <tbody>
            {shown.map(r => (
              <tr key={`${r.proposalId}-${r.project}-${r.dept}`}>
                <td>{r.dept}</td><td className="wrap"><strong>{r.project}</strong> <span className="small">{r.activity}</span></td><td className="small">{r.category}</td>
                <td className="small wrap">{r.types}{r.requestedCategory ? ` → ${r.requestedCategory}` : ''}<div className="muted">{r.briefCategories}</div></td>
                <td className="num">{mh(r.planning)}</td><td className="num">{mh(r.discipline)}</td><td className="num"><strong>{signed(r.net)}</strong></td>
                <td className="small wrap">{r.monthsChanged}</td><td className="small wrap">{r.reason}</td>
                <td><StatusBadge status={r.status} />{r.reviewComment && <div className="small muted">{r.reviewComment}</div>}</td>
                <td><StatusBadge status={r.implementation} /></td>
              </tr>
            ))}
            {shown.length === 0 && <tr><td colSpan={11} className="empty">No requests.</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  );
}
