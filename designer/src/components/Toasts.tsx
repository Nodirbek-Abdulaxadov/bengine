import { useToasts } from '../state/toast';

const ICON = { error: '⚠', success: '✓', info: 'ℹ' } as const;

/** Bottom-right stack of dismissible notifications. */
export function Toasts() {
  const toasts = useToasts((s) => s.toasts);
  const dismiss = useToasts((s) => s.dismiss);

  if (toasts.length === 0) {
    return null;
  }

  return (
    <div className="toasts">
      {toasts.map((t) => (
        <div key={t.id} className={`toast ${t.kind}`}>
          <div className="toast__head">
            <span className="toast__title">
              <span className="toast__icon">{ICON[t.kind]}</span>
              {t.title}
            </span>
            <button className="toast__close" onClick={() => dismiss(t.id)}>
              ✕
            </button>
          </div>
          {t.lines.length > 0 && (
            <ul className="toast__lines">
              {t.lines.map((line, i) => (
                <li key={i}>{line}</li>
              ))}
            </ul>
          )}
        </div>
      ))}
    </div>
  );
}
