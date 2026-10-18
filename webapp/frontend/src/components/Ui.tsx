import { useState, type ReactNode } from 'react';

/** Status chips always carry an icon and a word, never colour alone. */
const STATUS: Record<string, [string, string, string]> = {
  NotStarted: ['neutral', '○', 'Not started'],
  InProgress: ['info', '◐', 'In progress'],
  Submitted: ['good', '✓', 'Submitted'],
  NoChangeConfirmed: ['good', '✓', 'No change confirmed'],
  Returned: ['warn', '↩', 'Returned'],
  Draft: ['neutral', '✎', 'Draft'],
  Approved: ['good', '✓', 'Approved'],
  PartiallyApproved: ['good', '◑', 'Partly approved'],
  Rejected: ['bad', '✕', 'Rejected'],
  Implemented: ['good', '✓', 'Implemented'],
  'Partially implemented': ['warn', '◑', 'Partially implemented'],
  'Not implemented': ['bad', '✕', 'Not implemented'],
  'Approved - to implement': ['info', '→', 'Approved - to implement'],
  'Marked implemented - awaiting extract': ['info', '✓', 'Marked implemented'],
  'Awaiting decision': ['warn', '…', 'Awaiting decision'],
};

export function StatusBadge({ status }: { status: string }) {
  const [kind, icon, label] = STATUS[status] ?? ['neutral', '•', status];
  return <span className={`badge ${kind}`}><span aria-hidden>{icon}</span>{label}</span>;
}

export function Severity({ level }: { level: string }) {
  return <span className={`sev ${level.split(' ')[0]}`}>{level}</span>;
}

export function ErrorMsg({ error }: { error: string | null | undefined }) {
  return error ? <div className="msg error" role="alert">{error}</div> : null;
}

export function Loading() {
  return <div className="empty">Loading…</div>;
}

export const BLOCKS: [string, number, number][] = [['1–6', 0, 6], ['7–12', 6, 12], ['13–18', 12, 18], ['19–24', 18, 24], ['All', 0, 999]];

/** Month blocks 1-6, 7-12, 13-18, 19-24 or all, so the grid fits the screen. */
export function useMonthBlock(defaultIndex = 0): [number[] | null, ReactNode] {
  const [b, setB] = useState(defaultIndex);
  const node = (
    <span className="seg" role="group" aria-label="Months shown">
      {BLOCKS.map(([label], i) => <button key={label} className={i === b ? 'on' : ''} onClick={() => setB(i)}>Months {label}</button>)}
    </span>
  );
  return [[BLOCKS[b][1], BLOCKS[b][2]], node];
}

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <div className="modal-wrap" onClick={onClose}>
      <div className="modal" role="dialog" aria-label={title} onClick={e => e.stopPropagation()}>
        <div className="row" style={{ justifyContent: 'space-between', marginBottom: 12 }}>
          <h2 style={{ margin: 0 }}>{title}</h2>
          <button className="link" onClick={onClose} aria-label="Close">✕</button>
        </div>
        {children}
      </div>
    </div>
  );
}
