import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api } from './api';
import type { Cycle, Me, Role } from './types';

interface AppState {
  me: Me | null;
  cycles: Cycle[];
  cycle: Cycle | null;
  setCycleId: (id: number) => void;
  reloadCycles: () => Promise<void>;
  has: (...roles: Role[]) => boolean;
  toast: (text: string, kind?: 'ok' | 'error' | 'info') => void;
}

const Ctx = createContext<AppState | null>(null);

export function AppProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [cycles, setCycles] = useState<Cycle[]>([]);
  const [cycleId, setCycleIdState] = useState<number | null>(() => {
    try { const v = localStorage.getItem('cycleId'); return v ? Number(v) : null; } catch { return null; }
  });
  const [message, setMessage] = useState<{ text: string; kind: string } | null>(null);

  const reloadCycles = useCallback(async () => {
    const list = await api.get<Cycle[]>('/api/cycles');
    setCycles(list);
  }, []);

  useEffect(() => {
    api.get<Me>('/api/me').then(setMe).catch(() => setMe(null));
    reloadCycles().catch(() => undefined);
  }, [reloadCycles]);

  const setCycleId = (id: number) => {
    setCycleIdState(id);
    try { localStorage.setItem('cycleId', String(id)); } catch { /* storage unavailable */ }
  };
  const cycle = cycles.find(c => c.id === cycleId) ?? cycles.find(c => c.status !== 'Locked') ?? cycles[0] ?? null;
  const has = (...roles: Role[]) => !!me && roles.some(r => me.roles.includes(r));
  const toast = (text: string, kind: 'ok' | 'error' | 'info' = 'ok') => {
    setMessage({ text, kind });
    window.setTimeout(() => setMessage(m => (m?.text === text ? null : m)), kind === 'error' ? 8000 : 4000);
  };

  return (
    <Ctx.Provider value={{ me, cycles, cycle, setCycleId, reloadCycles, has, toast }}>
      {children}
      {message && <div className={`msg ${message.kind} toast`} role="status">{message.text}</div>}
    </Ctx.Provider>
  );
}

export function useApp() {
  const v = useContext(Ctx);
  if (!v) throw new Error('useApp outside AppProvider');
  return v;
}

/** Loads data for the current cycle and reloads when it changes; returns [data, error, reload]. */
export function useLoad<T>(url: string | null): [T | null, string | null, () => void] {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [n, setN] = useState(0);
  useEffect(() => {
    if (!url) return;
    let live = true;
    setError(null);
    api.get<T>(url).then(d => live && setData(d)).catch(e => live && setError(e.message));
    return () => { live = false; };
  }, [url, n]);
  return [data, error, () => setN(x => x + 1)];
}
