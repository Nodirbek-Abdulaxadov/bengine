import { useRef, useState, type KeyboardEvent } from 'react';
import { isNumericType, runQuery, type QueryResultDto, type Target } from '../../api/db';

interface Message {
  at: string;
  text: string;
  ok: boolean;
}

/**
 * The Query Tool: a SQL editor with line numbers bound to one database. Runs the
 * selected text, or everything when nothing is selected (F5 / Ctrl+Enter), and
 * shows the result grid plus a message log.
 */
export function QueryTab({ target, initialSql, label }: { target: Target; initialSql?: string; label: string }) {
  const [sql, setSql] = useState(initialSql ?? '');
  const [result, setResult] = useState<QueryResultDto | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [view, setView] = useState<'data' | 'messages'>('data');
  const [busy, setBusy] = useState(false);
  const [maxRows, setMaxRows] = useState(1000);
  const editorRef = useRef<HTMLTextAreaElement>(null);
  const gutterRef = useRef<HTMLDivElement>(null);

  const lines = sql.split('\n').length;

  const statement = () => {
    const el = editorRef.current;
    if (el && el.selectionStart !== el.selectionEnd) return sql.slice(el.selectionStart, el.selectionEnd);
    return sql;
  };

  const log = (text: string, ok: boolean) =>
    setMessages((m) => [{ at: new Date().toLocaleTimeString(), text, ok }, ...m].slice(0, 200));

  const run = async (explain = false) => {
    const text = statement().trim();
    if (!text) return;
    setBusy(true);
    try {
      const res = await runQuery(target, explain ? `EXPLAIN ${text}` : text, maxRows);
      setResult(res);
      setView(res.isQuery ? 'data' : 'messages');
      log(
        res.isQuery
          ? `Successfully run. Total query runtime: ${res.elapsedMilliseconds.toFixed(1)} msec. ${res.rows.length} rows${res.truncated ? ` (limited to ${maxRows})` : ''}.`
          : `Query returned successfully: ${res.rowsAffected} rows affected, ${res.elapsedMilliseconds.toFixed(1)} msec.`,
        true,
      );
    } catch (e) {
      log((e as Error).message, false);
      setView('messages');
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'F5' || ((e.ctrlKey || e.metaKey) && e.key === 'Enter')) {
      e.preventDefault();
      void run();
    } else if (e.key === 'Tab') {
      e.preventDefault();
      const el = e.currentTarget;
      const start = el.selectionStart;
      const next = `${sql.slice(0, start)}    ${sql.slice(el.selectionEnd)}`;
      setSql(next);
      requestAnimationFrame(() => el.setSelectionRange(start + 4, start + 4));
    }
  };

  const last = messages[0];

  return (
    <div className="qt">
      <div className="ex-toolbar">
        <button className="ex-btn" onClick={() => void run()} disabled={busy} title="Execute (F5 / Ctrl+Enter)">
          ▶ Execute
        </button>
        <button className="ex-btn ex-btn--ghost" onClick={() => void run(true)} disabled={busy} title="Explain">
          Explain
        </button>
        <span className="ex-toolbar__sep" />
        <span className="ex-dim">Limit</span>
        <select className="ex-select" value={maxRows} onChange={(e) => setMaxRows(Number(e.target.value))}>
          {[100, 500, 1000, 5000, 10000].map((n) => (
            <option key={n} value={n}>
              {n} rows
            </option>
          ))}
        </select>
        <div className="ex-toolbar__spacer" />
        <span className="ex-dim qt__target" title={label}>
          {label}
        </span>
      </div>

      <div className="qt__editor">
        <div className="qt__gutter" ref={gutterRef}>
          {Array.from({ length: lines }, (_, i) => (
            <div key={i}>{i + 1}</div>
          ))}
        </div>
        <textarea
          ref={editorRef}
          className="qt__input"
          value={sql}
          spellCheck={false}
          onChange={(e) => setSql(e.target.value)}
          onKeyDown={onKeyDown}
          onScroll={(e) => {
            if (gutterRef.current) gutterRef.current.scrollTop = e.currentTarget.scrollTop;
          }}
          placeholder="-- Write SQL here. F5 or Ctrl+Enter runs the selection, or everything when nothing is selected."
        />
      </div>

      <div className="qt__tabs">
        <button className={`qt__tab ${view === 'data' ? 'qt__tab--active' : ''}`} onClick={() => setView('data')}>
          Data Output
        </button>
        <button className={`qt__tab ${view === 'messages' ? 'qt__tab--active' : ''}`} onClick={() => setView('messages')}>
          Messages
        </button>
        <div className="ex-toolbar__spacer" />
        {busy ? <span className="ex-dim">Running…</span> : last ? <span className={last.ok ? 'qt__ok' : 'qt__err'}>{last.text}</span> : null}
      </div>

      {view === 'data' ? (
        <div className="ex-grid-wrap">
          {result?.isQuery ? (
            <table className="ex-grid">
              <thead>
                <tr>
                  <th className="ex-rowhead" />
                  {result.columns.map((c, i) => (
                    <th key={`${c}-${i}`}>
                      <div className="ex-grid__head">
                        <span>{c}</span>
                        <span className="ex-grid__type">{result.columnTypes?.[i] ?? ''}</span>
                      </div>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {result.rows.map((row, ri) => (
                  <tr key={ri}>
                    <td className="ex-rowhead">{ri + 1}</td>
                    {row.map((cell, ci) => (
                      <td
                        key={ci}
                        className={typeof cell === 'number' || isNumericType(result.columnTypes?.[ci]) ? 'ex-num' : undefined}
                        title={cell == null ? 'NULL' : String(cell)}
                      >
                        {cell === null || cell === undefined ? <span className="ex-null">[null]</span> : String(cell)}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="ws__empty">{result ? 'The statement returned no result set.' : 'Run a query to see its output.'}</div>
          )}
        </div>
      ) : (
        <div className="qt__messages">
          {messages.length === 0 ? <div className="ws__empty">No messages yet.</div> : null}
          {messages.map((m, i) => (
            <div key={i} className={m.ok ? 'qt__msg' : 'qt__msg qt__msg--err'}>
              <span className="ex-dim">{m.at}</span> {m.text}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
