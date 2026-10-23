import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { api } from './api';
import { AppProvider, useApp } from './context';
import { STAGE_LABEL, type Role } from './types';
import Login from './pages/Login';
import DashboardPage from './pages/Dashboard';
import ReviewPage from './pages/Review';
import PlannerPage from './pages/Planner';
import QueuePage from './pages/Queue';
import CycleControlPage from './pages/CycleControl';
import ReportsPage from './pages/Reports';
import AdminPage from './pages/Admin';
import './styles.css';

const NAV: { to: string; label: string; roles: Role[] }[] = [
  { to: '/', label: 'Dashboard', roles: ['CorporatePlanning', 'Planner', 'DisciplineManager', 'Viewer', 'Admin'] },
  { to: '/review', label: 'Discipline Review', roles: ['DisciplineManager', 'CorporatePlanning'] },
  { to: '/planner', label: 'My Projects', roles: ['Planner', 'CorporatePlanning'] },
  { to: '/queue', label: 'Approval Queue', roles: ['CorporatePlanning', 'Viewer'] },
  { to: '/cycle', label: 'Cycle Control', roles: ['CorporatePlanning', 'Admin', 'Viewer'] },
  { to: '/reports', label: 'Reports', roles: ['CorporatePlanning', 'Planner', 'DisciplineManager', 'Viewer', 'Admin'] },
  { to: '/admin', label: 'Administration', roles: ['Admin', 'CorporatePlanning'] },
];

function Shell() {
  const { me, cycles, cycle, setCycleId, has } = useApp();
  if (!me) return <div className="empty">Signing in…</div>;
  const signOut = async () => {
    if (me.authMode === 'Development') { await api.post('/api/dev/logout'); window.location.href = '/login'; }
    else window.location.href = '/.auth/logout';
  };
  if (me.roles.length === 0) return (
    <div className="login card">
      <h1>ENPPI Workload Forecast</h1>
      <p className="lead">You are signed in as <strong>{me.upn}</strong>, but this account has no access to the workload forecast yet.</p>
      <p>Ask Corporate Planning or the system administrator to add you (Administration → Users) with your role and departments.</p>
      <button onClick={signOut}>Sign out</button>
    </div>
  );
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand"><strong>Workload Forecast</strong><span>ENPPI Corporate Planning</span></div>
        <nav className="nav">
          {NAV.filter(n => has(...n.roles)).map(n => <NavLink key={n.to} to={n.to} end={n.to === '/'}>{n.label}</NavLink>)}
        </nav>
        <div className="who">
          <div><strong>{me.name}</strong></div>
          <div style={{ opacity: 0.8 }}>{me.roles.join(', ')}{me.departments.length ? ` · ${me.departments.join(', ')}` : ''}</div>
          <button className="sm" onClick={signOut}>Sign out</button>
        </div>
      </aside>
      <div className="main">
        <header className="topbar">
          <label className="row small" style={{ gap: 6 }}>
            Forecast cycle
            <select value={cycle?.id ?? ''} onChange={e => setCycleId(Number(e.target.value))} aria-label="Forecast cycle">
              {cycles.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          {cycle && <span className="badge info">Stage: {STAGE_LABEL[cycle.status]}</span>}
          {cycle?.reviewDue && <span className="small muted">Review due {new Date(cycle.reviewDue).toLocaleDateString('en-GB', { day: '2-digit', month: 'short' })}</span>}
          <span className="spacer" />
          <span className="small muted">Planner <span className="legend-swatch" style={{ background: 'var(--planner)' }} />
            · Discipline <span className="legend-swatch" style={{ background: 'var(--discipline)' }} />
            · Approved / after <span className="legend-swatch" style={{ background: 'var(--after)' }} /></span>
        </header>
        <main className="content">
          {!cycle && !has('CorporatePlanning') ? <div className="empty">No forecast cycle yet.</div> : (
            <Routes>
              <Route path="/" element={<DashboardPage />} />
              <Route path="/review" element={<ReviewPage />} />
              <Route path="/planner" element={<PlannerPage />} />
              <Route path="/queue" element={<QueuePage />} />
              <Route path="/cycle" element={<CycleControlPage />} />
              <Route path="/reports" element={<ReportsPage />} />
              <Route path="/admin" element={<AdminPage />} />
              <Route path="*" element={<Navigate to="/" />} />
            </Routes>
          )}
        </main>
      </div>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/*" element={<AppProvider><Shell /></AppProvider>} />
      </Routes>
    </BrowserRouter>
  </StrictMode>,
);
