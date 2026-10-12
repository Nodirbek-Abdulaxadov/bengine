import { useEffect, useState, type ReactNode } from 'react';
import type { Provider } from '../../api/db';
import {
  pgadminImport,
  pgadminPreview,
  saveServer,
  testServer,
  type PgAdminServer,
  type ServerDto,
  type ServerForm,
} from '../../api/explorer';

/** A centered modal with a title bar, body, and footer. */
export function Modal({
  title,
  width = 520,
  onClose,
  footer,
  children,
}: {
  title: string;
  width?: number;
  onClose: () => void;
  footer: ReactNode;
  children: ReactNode;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="dlg__backdrop" onMouseDown={onClose}>
      <div className="dlg" style={{ width }} onMouseDown={(e) => e.stopPropagation()} role="dialog" aria-label={title}>
        <div className="dlg__title">
          <span>{title}</span>
          <button className="dlg__close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="dlg__body">{children}</div>
        <div className="dlg__footer">{footer}</div>
      </div>
    </div>
  );
}

const DEFAULT_PORT: Record<Provider, number | null> = { PostgreSql: 5432, MySql: 3306, SqlServer: 1433, Sqlite: null };
const DEFAULT_DB: Record<Provider, string> = { PostgreSql: 'postgres', MySql: '', SqlServer: 'master', Sqlite: '' };
const DEFAULT_USER: Record<Provider, string> = { PostgreSql: 'postgres', MySql: 'root', SqlServer: 'sa', Sqlite: '' };

function toForm(server?: ServerDto): ServerForm {
  if (!server) {
    return {
      name: '',
      group: 'Servers',
      provider: 'PostgreSql',
      host: 'localhost',
      port: 5432,
      maintenanceDatabase: 'postgres',
      username: 'postgres',
      password: '',
      savePassword: true,
      filePath: '',
      options: '',
      comment: '',
    };
  }
  return {
    id: server.id,
    name: server.name,
    group: server.group,
    provider: server.provider,
    host: server.host ?? '',
    port: server.port ?? null,
    maintenanceDatabase: server.maintenanceDatabase ?? '',
    username: server.username ?? '',
    password: '',
    savePassword: server.savePassword,
    filePath: server.filePath ?? '',
    options: server.options ?? '',
    comment: server.comment ?? '',
  };
}

/** Register a new server or edit an existing one, pgAdmin-style (General / Connection / Advanced). */
export function ServerDialog({
  server,
  onClose,
  onSaved,
}: {
  server?: ServerDto;
  onClose: () => void;
  onSaved: (server: ServerDto) => void;
}) {
  const [form, setForm] = useState<ServerForm>(() => toForm(server));
  const [tab, setTab] = useState<'general' | 'connection' | 'advanced'>(server ? 'connection' : 'general');
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<{ text: string; ok: boolean } | null>(null);
  const isSqlite = form.provider === 'Sqlite';

  const set = <K extends keyof ServerForm>(key: K, value: ServerForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const pickProvider = (provider: Provider) =>
    setForm((f) => ({
      ...f,
      provider,
      port: DEFAULT_PORT[provider],
      maintenanceDatabase: DEFAULT_DB[provider],
      username: DEFAULT_USER[provider],
    }));

  const validate = (): string | null => {
    if (!form.name.trim()) return 'Name is required (General tab).';
    if (isSqlite && !form.filePath.trim()) return 'Database file is required.';
    if (!isSqlite && !form.host.trim()) return 'Host is required.';
    return null;
  };

  const test = async () => {
    const problem = validate();
    if (problem) return setStatus({ text: problem, ok: false });
    setBusy(true);
    setStatus(null);
    try {
      await testServer(form);
      setStatus({ text: 'Connection successful.', ok: true });
    } catch (e) {
      setStatus({ text: (e as Error).message, ok: false });
    } finally {
      setBusy(false);
    }
  };

  const save = async () => {
    const problem = validate();
    if (problem) {
      setStatus({ text: problem, ok: false });
      if (!form.name.trim()) setTab('general');
      return;
    }
    setBusy(true);
    try {
      onSaved(await saveServer(form));
    } catch (e) {
      setStatus({ text: (e as Error).message, ok: false });
      setBusy(false);
    }
  };

  return (
    <Modal
      title={server ? `${server.name} — Properties` : 'Register — Server'}
      onClose={onClose}
      footer={
        <>
          {status ? <span className={status.ok ? 'dlg__ok' : 'dlg__err'}>{status.text}</span> : <span />}
          <div className="dlg__actions">
            <button className="ex-btn ex-btn--ghost" onClick={() => void test()} disabled={busy}>
              Test connection
            </button>
            <button className="ex-btn ex-btn--ghost" onClick={onClose}>
              Cancel
            </button>
            <button className="ex-btn" onClick={() => void save()} disabled={busy}>
              Save
            </button>
          </div>
        </>
      }
    >
      <div className="dlg__tabs">
        {(['general', 'connection', 'advanced'] as const).map((t) => (
          <button key={t} className={`dlg__tab ${tab === t ? 'dlg__tab--active' : ''}`} onClick={() => setTab(t)}>
            {t[0].toUpperCase() + t.slice(1)}
          </button>
        ))}
      </div>

      {tab === 'general' ? (
        <div className="dlg__form">
          <Field label="Name">
            <input className="ex-input" autoFocus value={form.name} onChange={(e) => set('name', e.target.value)} placeholder="e.g. production" />
          </Field>
          <Field label="Server group">
            <input className="ex-input" value={form.group} onChange={(e) => set('group', e.target.value)} />
          </Field>
          <Field label="Comments">
            <textarea className="ex-input dlg__textarea" value={form.comment} onChange={(e) => set('comment', e.target.value)} />
          </Field>
        </div>
      ) : null}

      {tab === 'connection' ? (
        <div className="dlg__form">
          <Field label="Database type">
            <select className="ex-select" value={form.provider} onChange={(e) => pickProvider(e.target.value as Provider)}>
              <option value="PostgreSql">PostgreSQL</option>
              <option value="MySql">MySQL / MariaDB</option>
              <option value="SqlServer">Microsoft SQL Server</option>
              <option value="Sqlite">SQLite</option>
            </select>
          </Field>
          {isSqlite ? (
            <Field label="Database file">
              <input className="ex-input" value={form.filePath} onChange={(e) => set('filePath', e.target.value)} placeholder="/path/to/app.db" />
            </Field>
          ) : (
            <>
              <Field label="Host name/address">
                <input className="ex-input" value={form.host} onChange={(e) => set('host', e.target.value)} />
              </Field>
              <Field label="Port">
                <input
                  className="ex-input"
                  type="number"
                  value={form.port ?? ''}
                  onChange={(e) => set('port', e.target.value ? Number(e.target.value) : null)}
                />
              </Field>
              <Field label="Maintenance database">
                <input className="ex-input" value={form.maintenanceDatabase} onChange={(e) => set('maintenanceDatabase', e.target.value)} />
              </Field>
              <Field label="Username">
                <input className="ex-input" value={form.username} onChange={(e) => set('username', e.target.value)} />
              </Field>
              <Field label="Password">
                <input
                  className="ex-input"
                  type="password"
                  value={form.password}
                  onChange={(e) => set('password', e.target.value)}
                  placeholder={server?.hasPassword ? '(saved — leave blank to keep)' : ''}
                />
              </Field>
              <Field label="Save password?">
                <label className="dlg__check">
                  <input type="checkbox" checked={form.savePassword} onChange={(e) => set('savePassword', e.target.checked)} />
                  <span>Store it in ~/.config/bengine/servers.json (owner-only file)</span>
                </label>
              </Field>
            </>
          )}
        </div>
      ) : null}

      {tab === 'advanced' ? (
        <div className="dlg__form">
          <Field label="Connection parameters">
            <textarea
              className="ex-input dlg__textarea"
              value={form.options}
              onChange={(e) => set('options', e.target.value)}
              placeholder={
                form.provider === 'SqlServer'
                  ? 'TrustServerCertificate=True;Encrypt=True'
                  : form.provider === 'PostgreSql'
                    ? 'SSL Mode=Require;Timeout=10'
                    : 'Key=Value;Key2=Value2'
              }
            />
          </Field>
          <p className="dlg__hint">Extra driver parameters in connection-string form; the fields on the Connection tab override them.</p>
        </div>
      ) : null}
    </Modal>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="dlg__field">
      <span className="dlg__label">{label}</span>
      {children}
    </label>
  );
}

/** pgAdmin's "Connect to Server" prompt, shown when a server refuses the login. */
export function PasswordPrompt({
  server,
  error,
  onSubmit,
  onCancel,
}: {
  server: ServerDto;
  error?: string | null;
  onSubmit: (password: string, save: boolean) => void;
  onCancel: () => void;
}) {
  const [password, setPassword] = useState('');
  const [save, setSave] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => setBusy(false), [error]);

  const submit = () => {
    setBusy(true);
    onSubmit(password, save);
  };

  return (
    <Modal
      title="Connect to Server"
      width={440}
      onClose={onCancel}
      footer={
        <>
          {error ? <span className="dlg__err">{error}</span> : <span />}
          <div className="dlg__actions">
            <button className="ex-btn ex-btn--ghost" onClick={onCancel}>
              Cancel
            </button>
            <button className="ex-btn" onClick={submit} disabled={busy}>
              OK
            </button>
          </div>
        </>
      }
    >
      <p className="dlg__text">
        Please enter the password for the user <b>{server.username ?? '—'}</b> to connect the server <b>{server.name}</b>.
      </p>
      <div className="dlg__form">
        <Field label="Password">
          <input
            className="ex-input"
            type="password"
            autoFocus
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && submit()}
          />
        </Field>
        <Field label="">
          <label className="dlg__check">
            <input type="checkbox" checked={save} onChange={(e) => setSave(e.target.checked)} />
            <span>Save Password</span>
          </label>
        </Field>
      </div>
    </Modal>
  );
}

/** Lists the servers registered in the local pgAdmin and imports the new ones. */
export function ImportDialog({ onClose, onImported }: { onClose: () => void; onImported: (count: number) => void }) {
  const [state, setState] = useState<{ found: boolean; path?: string; servers: PgAdminServer[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    pgadminPreview()
      .then(setState)
      .catch((e: Error) => setError(e.message));
  }, []);

  const fresh = state?.servers.filter((s) => !s.exists) ?? [];

  const run = async () => {
    setBusy(true);
    try {
      const result = await pgadminImport();
      onImported(result.imported);
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  return (
    <Modal
      title="Import Servers from pgAdmin"
      width={600}
      onClose={onClose}
      footer={
        <>
          {error ? <span className="dlg__err">{error}</span> : <span className="dlg__hint">Passwords are not imported — pgAdmin encrypts them. You will be asked on first connect.</span>}
          <div className="dlg__actions">
            <button className="ex-btn ex-btn--ghost" onClick={onClose}>
              Cancel
            </button>
            <button className="ex-btn" onClick={() => void run()} disabled={busy || fresh.length === 0}>
              Import {fresh.length > 0 ? `${fresh.length} server(s)` : ''}
            </button>
          </div>
        </>
      }
    >
      {!state && !error ? <p className="dlg__text">Looking for pgAdmin…</p> : null}
      {state && !state.found ? <p className="dlg__text">No pgAdmin 4 configuration was found on this machine.</p> : null}
      {state?.found ? (
        <>
          <p className="dlg__hint">{state.path}</p>
          <table className="ex-deftable">
            <thead>
              <tr>
                <th>Name</th>
                <th>Group</th>
                <th>Host</th>
                <th>Database</th>
                <th>User</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {state.servers.map((s) => (
                <tr key={`${s.name}@${s.host}:${s.port}`} style={{ opacity: s.exists ? 0.5 : 1 }}>
                  <td>{s.name}</td>
                  <td>{s.group}</td>
                  <td>
                    {s.host}:{s.port}
                  </td>
                  <td>{s.maintenanceDatabase}</td>
                  <td>{s.username}</td>
                  <td>{s.exists ? 'already added' : 'new'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
    </Modal>
  );
}
