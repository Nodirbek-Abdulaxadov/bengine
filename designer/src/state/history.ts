import { useSyncExternalStore } from 'react';
import { toSchemaDocument } from './mapping';
import { useDesigner, type DesignerSnapshot, type DesignerState } from './store';

const STORAGE_KEY = 'bengine-designer';
const MAX_HISTORY = 100;

let past: DesignerSnapshot[] = [];
let future: DesignerSnapshot[] = [];
let traveling = false;

const snapshotOf = (state: DesignerState | DesignerSnapshot): DesignerSnapshot =>
  structuredClone({
    schemaName: state.schemaName,
    tables: state.tables,
    foreignKeys: state.foreignKeys,
    views: state.views,
    sequences: state.sequences,
    triggers: state.triggers,
  });

// The schema.json projection is the identity used for history: layout moves and
// selection changes do not create undo steps, only structural edits do.
const signature = (state: DesignerSnapshot): string =>
  JSON.stringify(toSchemaDocument(state));

// --- history meta exposed to React via useSyncExternalStore ---
let meta = { canUndo: false, canRedo: false };
const listeners = new Set<() => void>();
const refresh = () => {
  meta = { canUndo: past.length > 0, canRedo: future.length > 0 };
  listeners.forEach((l) => l());
};

const persist = (state: DesignerState) => {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshotOf(state)));
  } catch {
    // Storage may be unavailable (private mode, quota) — ignore.
  }
};

useDesigner.subscribe((state, prev) => {
  persist(state);
  if (traveling) {
    return;
  }
  if (signature(state) !== signature(prev)) {
    past.push(snapshotOf(prev));
    if (past.length > MAX_HISTORY) {
      past.shift();
    }
    future = [];
    refresh();
  }
});

export function undo(): void {
  const previous = past.pop();
  if (!previous) {
    return;
  }
  future.push(snapshotOf(useDesigner.getState()));
  traveling = true;
  useDesigner.getState().replaceAll(previous);
  traveling = false;
  refresh();
}

export function redo(): void {
  const next = future.pop();
  if (!next) {
    return;
  }
  past.push(snapshotOf(useDesigner.getState()));
  traveling = true;
  useDesigner.getState().replaceAll(next);
  traveling = false;
  refresh();
}

/** Restores the last autosaved design from localStorage, if any. */
export function loadPersisted(): void {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return;
    }
    const saved = JSON.parse(raw) as DesignerSnapshot;
    traveling = true;
    useDesigner.getState().replaceAll(saved);
    traveling = false;
  } catch {
    // Corrupt or unreadable storage — start empty.
  }
}

export function useHistory(): { canUndo: boolean; canRedo: boolean } {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => meta,
  );
}
