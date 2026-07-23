import { useRef, useState } from 'react';
import { importSql } from '../api/client';
import { fromSchemaDocument } from '../state/mapping';
import { useDesigner } from '../state/store';

/** Dialect options for the import dropdown; "auto" lets the backend detect it. */
const DIALECTS: { value: string; label: string }[] = [
  { value: 'auto', label: 'Avto-aniqlash' },
  { value: 'PostgreSql', label: 'PostgreSQL' },
  { value: 'SqlServer', label: 'SQL Server' },
  { value: 'MySql', label: 'MySQL' },
  { value: 'Sqlite', label: 'SQLite' },
];

interface ImportSqlDialogProps {
  onClose: () => void;
}

/**
 * Modal that imports raw SQL DDL. The SQL is parsed by the local Bengine backend
 * (which detects the dialect when set to "auto") into a schema.json document,
 * then loaded onto the canvas, replacing the current design.
 */
export function ImportSqlDialog({ onClose }: ImportSqlDialogProps) {
  const replaceAll = useDesigner((s) => s.replaceAll);
  const fileInput = useRef<HTMLInputElement>(null);
  const [sql, setSql] = useState('');
  const [dialect, setDialect] = useState('auto');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const loadFile = async (file: File) => {
    setSql(await file.text());
    setError('');
  };

  const run = async () => {
    if (sql.trim() === '') {
      setError('SQL matnini kiriting yoki .sql fayl yuklang.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      const result = await importSql(sql, dialect);
      replaceAll(fromSchemaDocument(result.schema));
      onClose();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="modal__backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal__head">
          <strong>SQL import</strong>
          <button className="modal__close" onClick={onClose} title="Yopish">
            ✕
          </button>
        </div>

        <p className="muted small">
          CREATE TABLE / INDEX / ALTER TABLE DDL sini joylashtiring. Backend (localhost) dialektni
          aniqlab, sxemaga aylantiradi. Import mavjud dizaynni almashtiradi.
        </p>

        <div className="row">
          <label className="small muted">Dialekt</label>
          <select value={dialect} onChange={(e) => setDialect(e.target.value)}>
            {DIALECTS.map((d) => (
              <option key={d.value} value={d.value}>
                {d.label}
              </option>
            ))}
          </select>
          <div className="toolbar__spacer" />
          <button onClick={() => fileInput.current?.click()}>.sql fayl</button>
          <input
            ref={fileInput}
            type="file"
            accept=".sql,text/plain"
            hidden
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) {
                void loadFile(file);
              }
              e.target.value = '';
            }}
          />
        </div>

        <textarea
          className="modal__sql"
          value={sql}
          onChange={(e) => setSql(e.target.value)}
          placeholder={'CREATE TABLE users (\n  id BIGINT PRIMARY KEY,\n  email VARCHAR(320) NOT NULL\n);'}
          spellCheck={false}
        />

        {error && <div className="modal__error small">✗ {error}</div>}

        <div className="modal__actions">
          <button onClick={onClose}>Bekor qilish</button>
          <button className="primary" disabled={busy} onClick={() => void run()}>
            {busy ? 'import...' : 'Import qilish'}
          </button>
        </div>
      </div>
    </div>
  );
}
