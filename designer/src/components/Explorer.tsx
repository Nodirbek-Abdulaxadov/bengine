import { useCallback, useEffect, useRef, useState } from 'react';
import './explorer/explorer.css';
import { NeedsPasswordError, type Target } from '../api/db';
import { connectServer, deleteServer, treeProperties, type PropertiesDto, type ServerDto } from '../api/explorer';
import { ObjectTree, type Located } from './explorer/ObjectTree';
import { ImportDialog, PasswordPrompt, ServerDialog } from './explorer/Dialogs';
import { PropertiesPanel, SqlView } from './explorer/Panels';
import { DataTab } from './explorer/DataTab';
import { QueryTab } from './explorer/QueryTab';
import { TreeIcon } from './explorer/TreeIcon';

interface Doc {
  id: string;
  type: 'data' | 'query';
  title: string;
  server: ServerDto;
  target: Target;
  table?: { name: string; schema: string | null };
  sql?: string;
  label: string;
}

type Dialog = { type: 'server'; server?: ServerDto } | { type: 'import' } | null;

interface Prompt {
  server: ServerDto;
  error?: string | null;
  resolve: (ok: boolean) => void;
}

let queryCounter = 0;

/**
 * The database explorer workspace, laid out like pgAdmin: the Object Explorer
 * tree on the left; on the right, Properties and SQL for the selected node plus
 * closable View/Edit Data and Query Tool tabs.
 */
export function Explorer() {
  const [reloadToken, setReloadToken] = useState(0);
  const [selection, setSelection] = useState<Located | null>(null);
  const [properties, setProperties] = useState<{ loading: boolean; data?: PropertiesDto; error?: string }>({ loading: false });
  const [docs, setDocs] = useState<Doc[]>([]);
  const [active, setActive] = useState<string>('properties');
  const [dialog, setDialog] = useState<Dialog>(null);
  const [prompt, setPrompt] = useState<Prompt | null>(null);
  const [leftWidth, setLeftWidth] = useState(340);
  const dragging = useRef(false);
  const selectionRef = useRef<Located | null>(null);

  // ---- password prompt, shared by the tree and the tabs
  const requestPassword = useCallback(
    (server: ServerDto, message?: string) =>
      new Promise<boolean>((resolve) => setPrompt({ server, error: message, resolve })),
    [],
  );

  const submitPassword = async (password: string, save: boolean) => {
    if (!prompt) return;
    try {
      await connectServer(prompt.server.id, password, save);
      prompt.resolve(true);
      setPrompt(null);
      if (save) setReloadToken((t) => t + 1);
    } catch (e) {
      setPrompt({ ...prompt, error: e instanceof NeedsPasswordError ? 'Password authentication failed.' : (e as Error).message });
    }
  };

  const cancelPassword = () => {
    prompt?.resolve(false);
    setPrompt(null);
  };

  /** Makes sure the server accepts our credentials before opening a tab on it. */
  const ensureConnected = useCallback(
    async (server: ServerDto): Promise<boolean> => {
      try {
        await connectServer(server.id);
        return true;
      } catch (e) {
        if (e instanceof NeedsPasswordError) return requestPassword(server);
        alert((e as Error).message);
        return false;
      }
    },
    [requestPassword],
  );

  // ---- selection → Properties / SQL
  useEffect(() => {
    selectionRef.current = selection;
    if (!selection) return;
    const { server, node } = selection;
    setProperties({ loading: true });
    treeProperties(server.id, node.ref)
      .then((data) => {
        if (selectionRef.current === selection) setProperties({ loading: false, data });
      })
      .catch((e: Error) => {
        if (selectionRef.current !== selection) return;
        setProperties({
          loading: false,
          error: e instanceof NeedsPasswordError ? 'Not connected. Expand the server (or right-click → Connect Server) to sign in.' : e.message,
        });
      });
  }, [selection]);

  const onSelect = useCallback((located: Located) => {
    setSelection(located);
    setActive((a) => (a === 'properties' || a === 'sql' ? a : a));
  }, []);

  // ---- tabs
  const openDoc = (doc: Doc) => {
    setDocs((d) => (d.some((x) => x.id === doc.id) ? d : [...d, doc]));
    setActive(doc.id);
  };

  const onOpenData = useCallback((located: Located) => {
    const { server, node } = located;
    const database = node.ref.database ?? null;
    const schema = node.ref.schema ?? null;
    const name = node.ref.name!;
    openDoc({
      id: `data:${server.id}:${database}:${schema}.${name}`,
      type: 'data',
      title: name,
      server,
      target: { serverId: server.id, database },
      table: { name, schema },
      label: `${server.name} / ${database ?? ''}${schema ? ` / ${schema}` : ''}`,
    });
  }, []);

  const onOpenQuery = useCallback(
    async (server: ServerDto, database: string | null, sql?: string) => {
      if (!(await ensureConnected(server))) return;
      const db = database ?? server.maintenanceDatabase ?? null;
      queryCounter += 1;
      openDoc({
        id: `query:${queryCounter}`,
        type: 'query',
        title: `${db ?? server.name}/${server.username ?? ''}@${server.name}`,
        server,
        target: { serverId: server.id, database: db },
        sql,
        label: `${server.name} › ${db ?? '(default)'}`,
      });
    },
    [ensureConnected],
  );

  const closeDoc = (id: string) => {
    setDocs((d) => {
      const index = d.findIndex((x) => x.id === id);
      const next = d.filter((x) => x.id !== id);
      if (active === id) setActive(next[index - 1]?.id ?? next[index]?.id ?? 'properties');
      return next;
    });
  };

  // ---- server management
  const onDeleteServer = useCallback(async (server: ServerDto) => {
    if (!confirm(`Remove the server "${server.name}" from the list? The database itself is not touched.`)) return;
    await deleteServer(server.id);
    setDocs((d) => d.filter((x) => x.server.id !== server.id));
    setSelection(null);
    setReloadToken((t) => t + 1);
  }, []);

  // ---- splitter
  useEffect(() => {
    const move = (e: MouseEvent) => {
      if (dragging.current) setLeftWidth(Math.max(220, Math.min(720, e.clientX)));
    };
    const up = () => {
      dragging.current = false;
      document.body.style.cursor = '';
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
    return () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
  }, []);

  const crumbs = selection ? breadcrumb(selection) : [];
  const sql = properties.data?.sql;

  return (
    <div className="ex">
      <div className="ex-body">
        <div className="ex-left" style={{ width: leftWidth }}>
          <ObjectTree
            reloadToken={reloadToken}
            onSelect={onSelect}
            onOpenData={onOpenData}
            onOpenQuery={(s, db, q) => void onOpenQuery(s, db, q)}
            onRegister={() => setDialog({ type: 'server' })}
            onEditServer={(server) => setDialog({ type: 'server', server })}
            onDeleteServer={(server) => void onDeleteServer(server)}
            onImport={() => setDialog({ type: 'import' })}
            requestPassword={requestPassword}
          />
        </div>
        <div
          className="ex-splitter"
          onMouseDown={() => {
            dragging.current = true;
            document.body.style.cursor = 'col-resize';
          }}
        />
        <div className="ex-main">
          <div className="ws__tabs">
            <WsTab id="properties" active={active} onClick={setActive} label="Properties" />
            <WsTab id="sql" active={active} onClick={setActive} label="SQL" />
            {docs.map((d) => (
              <WsTab
                key={d.id}
                id={d.id}
                active={active}
                onClick={setActive}
                onClose={() => closeDoc(d.id)}
                label={d.title}
                icon={d.type === 'data' ? 'Table' : 'Function'}
                title={`${d.type === 'data' ? 'View/Edit Data' : 'Query Tool'} — ${d.label}`}
              />
            ))}
            <div className="ex-toolbar__spacer" />
            {selection ? (
              <button
                className="ws__action"
                title="Open a Query Tool on the selected database"
                onClick={() =>
                  void onOpenQuery(selection.server, selection.node.ref.database ?? selection.server.maintenanceDatabase ?? null)
                }
              >
                ⌨ Query Tool
              </button>
            ) : null}
          </div>

          {(active === 'properties' || active === 'sql') && (
            <div className="ws__crumbs">
              {selection ? (
                <>
                  <TreeIcon kind={selection.node.ref.kind} provider={selection.server.provider} badge={selection.node.badge} />
                  {crumbs.map((c, i) => (
                    <span key={i} className={i === crumbs.length - 1 ? 'ws__crumb ws__crumb--last' : 'ws__crumb'}>
                      {c}
                    </span>
                  ))}
                </>
              ) : (
                <span className="ex-dim">Select an object in the Object Explorer.</span>
              )}
            </div>
          )}

          <div className="ws__body">
            {active === 'properties' ? (
              properties.loading ? (
                <div className="ws__empty">Loading…</div>
              ) : properties.error ? (
                <div className="ex-error">{properties.error}</div>
              ) : properties.data ? (
                <PropertiesPanel data={properties.data} />
              ) : (
                <Welcome onRegister={() => setDialog({ type: 'server' })} onImport={() => setDialog({ type: 'import' })} />
              )
            ) : null}

            {active === 'sql' ? (
              properties.loading ? (
                <div className="ws__empty">Loading…</div>
              ) : sql ? (
                <div className="ex-tabpanel">
                  <div className="ex-toolbar">
                    <span className="ex-dim">{properties.data?.title}</span>
                    <div className="ex-toolbar__spacer" />
                    <button className="ex-btn ex-btn--ghost" onClick={() => void navigator.clipboard?.writeText(sql)}>
                      Copy
                    </button>
                    {selection ? (
                      <button
                        className="ex-btn ex-btn--ghost"
                        onClick={() => void onOpenQuery(selection.server, selection.node.ref.database ?? null, sql)}
                      >
                        Open in Query Tool
                      </button>
                    ) : null}
                  </div>
                  <SqlView sql={sql} />
                </div>
              ) : (
                <div className="ws__empty">{selection ? 'No SQL for this object.' : 'Select an object to see its SQL.'}</div>
              )
            ) : null}

            {docs.map((d) => (
              <div key={d.id} className="ws__doc" style={{ display: active === d.id ? 'flex' : 'none' }}>
                {d.type === 'data' && d.table ? (
                  <DataTab target={d.target} table={d.table} />
                ) : (
                  <QueryTab target={d.target} initialSql={d.sql} label={d.label} />
                )}
              </div>
            ))}
          </div>
        </div>
      </div>

      {dialog?.type === 'server' ? (
        <ServerDialog
          server={dialog.server}
          onClose={() => setDialog(null)}
          onSaved={() => {
            setDialog(null);
            setReloadToken((t) => t + 1);
          }}
        />
      ) : null}
      {dialog?.type === 'import' ? (
        <ImportDialog
          onClose={() => setDialog(null)}
          onImported={() => {
            setDialog(null);
            setReloadToken((t) => t + 1);
          }}
        />
      ) : null}
      {prompt ? <PasswordPrompt server={prompt.server} error={prompt.error} onSubmit={(p, s) => void submitPassword(p, s)} onCancel={cancelPassword} /> : null}
    </div>
  );
}

function WsTab({
  id,
  active,
  onClick,
  onClose,
  label,
  icon,
  title,
}: {
  id: string;
  active: string;
  onClick: (id: string) => void;
  onClose?: () => void;
  label: string;
  icon?: string;
  title?: string;
}) {
  return (
    <div
      className={`ws__tab ${active === id ? 'ws__tab--active' : ''}`}
      onClick={() => onClick(id)}
      onAuxClick={(e) => e.button === 1 && onClose?.()}
      title={title}
    >
      {icon ? <TreeIcon kind={icon} /> : null}
      <span>{label}</span>
      {onClose ? (
        <button
          className="ws__tab-close"
          onClick={(e) => {
            e.stopPropagation();
            onClose();
          }}
          aria-label="Close tab"
        >
          ✕
        </button>
      ) : null}
    </div>
  );
}

function Welcome({ onRegister, onImport }: { onRegister: () => void; onImport: () => void }) {
  return (
    <div className="ws__welcome">
      <h2>Bengine Database Explorer</h2>
      <p>Browse PostgreSQL, MySQL, SQL Server, and SQLite: databases, schemas, tables, functions, roles — with data editing and a query tool.</p>
      <div className="ws__welcome-actions">
        <button className="ex-btn" onClick={onRegister}>
          Register Server
        </button>
        <button className="ex-btn ex-btn--ghost" onClick={onImport}>
          Import from pgAdmin / DataGrip
        </button>
      </div>
      <p className="ex-dim">Select any object in the tree to see its properties and SQL. Double-click a table to view its data.</p>
    </div>
  );
}

function breadcrumb({ server, node }: Located): string[] {
  const parts = [server.name];
  const r = node.ref;
  if (r.database && node.ref.kind !== 'Database') parts.push(r.database);
  if (r.schema && !['Schema', 'Catalog'].includes(r.kind) && r.schema !== r.database) parts.push(r.schema);
  if (r.relation && r.relation !== r.name && !['Table', 'View', 'MaterializedView', 'ForeignTable'].includes(r.kind)) parts.push(r.relation);
  if (r.kind !== 'Server') parts.push(node.label);
  return parts;
}
