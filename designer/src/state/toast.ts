import { create } from 'zustand';

export type ToastKind = 'error' | 'success' | 'info';

export interface Toast {
  id: string;
  kind: ToastKind;
  title: string;
  lines: string[];
}

interface ToastState {
  toasts: Toast[];
  push: (kind: ToastKind, title: string, lines: string[]) => void;
  dismiss: (id: string) => void;
}

const useToastStore = create<ToastState>((set) => ({
  toasts: [],
  push: (kind, title, lines) => {
    const id = crypto.randomUUID();
    set((s) => ({ toasts: [...s.toasts, { id, kind, title, lines }] }));
    if (kind !== 'error') {
      setTimeout(
        () => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
        3500,
      );
    }
  },
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
}));

export const useToasts = useToastStore;

/** Fire-and-forget notification helpers usable outside React. */
export const toast = {
  error: (title: string, lines: string[] = []) => useToastStore.getState().push('error', title, lines),
  success: (title: string, lines: string[] = []) => useToastStore.getState().push('success', title, lines),
  info: (title: string, lines: string[] = []) => useToastStore.getState().push('info', title, lines),
};
