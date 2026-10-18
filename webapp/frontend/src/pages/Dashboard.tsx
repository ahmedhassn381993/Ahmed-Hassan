import { Link } from 'react-router-dom';
import { DivergingBars, LineChart, Waterfall } from '../components/Charts';
import { ErrorMsg, Loading, Severity, StatusBadge } from '../components/Ui';
import { useApp, useLoad } from '../context';
import { mh, monthLabel, pct, signed, signedPct } from '../format';
import type { Dashboard } from '../types';

export default function DashboardPage() {
  const { cycle } = useApp();
  const [d, error] = useLoad<Dashboard>(cycle ? `/api/cycles/${cycle.id}/dashboard` : null);
  if (error) return <ErrorMsg error={error} />;
  if (!d || !cycle) return <Loading />;
  const k = d.kpis;
  const net = k.discipline - k.planning;
  const labels = d.monthly.map((_, i) => monthLabel(cycle.firstMonth, i));
  const peak = d.monthly.reduce((a, m, i) => (m.discipline > a.v ? { v: m.discipline, i } : a), { v: 0, i: 0 });
  const depts = d.departments.filter(x => Math.abs(x.net) > 0.5).sort((a, b) => b.net - a.net);
  return (
    <div>
      <h1>{cycle.name} – workload forecast</h1>
      <p className="lead">
        Departments asked for <strong>{signed(net)} MH ({signedPct(k.planning ? net / k.planning : 0)})</strong> on a planning input of {mh(k.planning)} MH;
        {' '}{k.departmentsSubmitted} of {k.departments} departments have answered and {k.pendingDecisions} requests wait for a decision.
        {peak.v > k.capacityPerMonth && <> Discipline-adjusted workload peaks in {labels[peak.i]} at {pct(peak.v / k.capacityPerMonth)} of capacity.</>}
      </p>
      <div className="tiles">
        <div className="tile planner"><div className="label">Planning input</div><div className="value">{mh(k.planning)}</div><div className="sub">MH over {cycle.monthCount} months</div></div>
        <div className="tile discipline"><div className="label">Discipline-adjusted</div><div className="value">{mh(k.discipline)}</div><div className="sub">{signed(net)} MH requested</div></div>
        <div className="tile after"><div className="label">{cycle.hasAfterData ? 'After implementation' : 'Approved (final)'}</div><div className="value">{mh(cycle.hasAfterData ? k.after : k.final)}</div><div className="sub">{cycle.hasAfterData ? 'implemented' : 'approved'}: {pct(k.implementationRate)} of requested change</div></div>
        <div className="tile"><div className="label">Requests</div><div className="value">{k.concernItems}</div><div className="sub">{k.pendingDecisions} awaiting decision · {k.newAllocations} new allocations</div></div>
        <div className="tile"><div className="label">Projects by severity</div><div className="value">{k.critical} / {k.high}</div><div className="sub">Critical / High</div></div>
        <div className="tile"><div className="label">Departments answered</div><div className="value">{k.departmentsSubmitted} / {k.departments}</div><div className="sub"><Link to="/cycle">status board</Link></div></div>
      </div>
      <div className="card">
        <LineChart title="Company workload by month – three scenarios against capacity (MH)" labels={labels}
          series={[
            { name: 'Planning input', color: 'var(--planner)', values: d.monthly.map(m => m.planning) },
            { name: 'Discipline-adjusted', color: 'var(--discipline)', values: d.monthly.map(m => m.discipline) },
            { name: cycle.hasAfterData ? 'After implementation' : 'Approved (final)', color: 'var(--after)', values: d.monthly.map(m => (cycle.hasAfterData ? m.after : m.final)) },
            { name: 'Capacity', color: 'var(--capacity)', values: d.monthly.map(m => m.capacity), dashed: true },
          ]} />
      </div>
      <div className="grid2" style={{ marginTop: 16 }}>
        <div className="card">
          <Waterfall title={`Net deviation bridge: ${mh(k.planning)} → ${mh(k.discipline)} MH`} totalLabel="Net deviation"
            steps={[{ label: 'New work', value: d.bridge.newWork }, { label: 'Increase', value: d.bridge.increase },
                    { label: 'Extension', value: d.bridge.extension }, { label: 'Reduction', value: d.bridge.reduction },
                    { label: 'Removed', value: d.bridge.removed }]} />
          <p className="small muted">Extension includes work requested before the planned start. Each request falls in one bucket, so the steps add up to the net deviation.</p>
        </div>
        <div className="card">
          <DivergingBars title="Net deviation by department (MH)"
            rows={depts.map(x => ({ label: `${x.deptCode} ${x.department}`, value: x.net, note: signedPct(x.deviationPct) }))} />
          {depts.length === 0 && <div className="empty">No department has requested a change yet.</div>}
        </div>
      </div>
      <div className="grid2" style={{ marginTop: 16 }}>
        <div className="card">
          <h3>Top projects by severity</h3>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Project</th><th>Severity</th><th className="num">Depts</th><th className="num">Net MH</th><th>Concern types</th></tr></thead>
              <tbody>
                {d.topProjects.map(p => (
                  <tr key={p.projectCode}><td className="wrap"><strong>{p.projectCode}</strong> {p.name}</td><td><Severity level={p.level} /> <span className="small muted">{p.points} pts</span></td>
                    <td className="num">{p.deptsRaising}</td><td className="num">{signed(p.net)}</td><td className="wrap small">{p.types}</td></tr>
                ))}
                {d.topProjects.length === 0 && <tr><td colSpan={5} className="empty">No concerns raised yet.</td></tr>}
              </tbody>
            </table>
          </div>
        </div>
        <div className="card">
          <h3>Departments: utilisation and review status</h3>
          <div className="table-wrap" style={{ maxHeight: 360 }}>
            <table>
              <thead><tr><th>Department</th><th>Review</th><th className="num">Net MH</th><th className="num">Firm util.</th><th className="num">Overload months</th></tr></thead>
              <tbody>
                {d.departments.map(x => (
                  <tr key={x.deptCode}><td>{x.deptCode} {x.department}</td><td><StatusBadge status={x.reviewStatus} /></td>
                    <td className="num">{signed(x.net)}</td>
                    <td className={`num${x.firmUtilisationDiscipline > 1 ? ' over' : ''}`} style={x.firmUtilisationDiscipline > 1 ? { color: 'var(--bad)', fontWeight: 600 } : undefined}>{pct(x.firmUtilisationDiscipline)}</td>
                    <td className="num">{x.overloadMonths}</td></tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </div>
  );
}
