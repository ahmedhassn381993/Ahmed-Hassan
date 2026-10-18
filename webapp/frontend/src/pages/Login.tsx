import { useEffect, useState } from 'react';
import { api } from '../api';
import { ErrorMsg } from '../components/Ui';

interface DevUser { upn: string; displayName: string; role: string; deptCodes?: string | null }

/** Development sign-in only. In production App Service signs users in with their ENPPI Microsoft account. */
export default function Login() {
  const [users, setUsers] = useState<DevUser[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api.get<DevUser[]>('/api/dev/users').then(setUsers)
      .catch(() => { window.location.href = '/.auth/login/aad?post_login_redirect_uri=/'; });
  }, []);
  const login = async (upn: string) => {
    try { await api.post('/api/dev/login', { upn }); window.location.href = '/'; } catch (e) { setError((e as Error).message); }
  };
  return (
    <div className="login card">
      <h1>ENPPI Workload Forecast</h1>
      <p className="lead">Development sign-in. Choose a demonstration user; in production you sign in with your ENPPI Microsoft account.</p>
      <ErrorMsg error={error} />
      {users.map(u => (
        <button key={u.upn} className="user" onClick={() => login(u.upn)}>
          <span><strong>{u.displayName}</strong><br /><span className="small muted">{u.upn}</span></span>
          <span className="badge info">{u.role}{u.deptCodes ? ` · ${u.deptCodes}` : ''}</span>
        </button>
      ))}
    </div>
  );
}
