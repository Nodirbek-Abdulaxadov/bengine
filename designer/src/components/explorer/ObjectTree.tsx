import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react';
import { NeedsPasswordError } from '../../api/db';
import {
  disconnectServer,
  listServers,
  RELATION_KINDS,
  treeChildren,
  type ServerDto,
  type TreeNodeDto,
} from '../../api/explorer';
import { TreeIcon } from './TreeIcon';

/** A node together with the server it belongs to. */
export interface Located {
  server: ServerDto;
  node: TreeNodeDto;
}

interface MenuItem {
  label: string;
  action: () => void;
  danger?: boolean;
  separator?: boolean;
}

interface Row {
  key: string;
  depth: number;
  kind: 'group' | 'node';
  group?: string;
  located?: Located;
  parentKey: string | null;
  expandable: boolean;
}

const serverRoot = (server: ServerDto): TreeNodeDto => ({
  ref: { kind: 'Server' },
  label: server.name,
  hasChildren: true,
  isFolder: false,
  id: 'Server',
});

const keyOf = (server: ServerDto, node: TreeNodeDto) => `${server.id}::${node.id}`;

/**
 * The object explorer tree: registered servers grouped like pgAdmin's
 * "Servers (N)", each expanding lazily into databases, schemas, and objects.
 * Supports keyboard navigation, a filter over loaded nodes, a context menu, and
 * prompts for a password when a server refuses the login.
 */
export function ObjectTree({
  reloadToken,
  onSelect,
  onOpenData,
  onOpenQuery,
  onRegister,
  onEditServer,
  onDeleteServer,
  onImport,
  requestPassword,
}: {
  reloadToken: number;
  onSelect: (located: Located) => void;
  onOpenData: (located: Located) => void;
  onOpenQuery: (server: ServerDto, database: string | null, sql?: string) => void;
  onRegister: () => void;
  onEditServer: (server: ServerDto) => void;
  onDeleteServer: (server: ServerDto) => void;
  onImport: () => void;
  requestPassword: (server: ServerDto, message?: string) => Promise<boolean>;
}) {
  const [servers, setServers] = useState<ServerDto[]>([]);
  const [children, setChildren] = useState<Record<string, TreeNodeDto[]>>({});
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(['group:Servers']));
  const [loading, setLoading] = useState<Set<string>>(() => new Set());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [connected, setConnected] = useState<Set<string>>(() => new Set());
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [menu, setMenu] = useState<{ x: number; y: number; items: MenuItem[] } | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  const refreshServers = useCallback(async () => {
    try {
      const list = await listServers();
      setServers(list);
      setConnected((prev) => new Set([...prev, ...list.filter((s) => s.connected).map((s) => s.id)]));
      setListError(null);
      setExpanded((prev) => {
        const next = new Set(prev);
        list.forEach((s) => next.add(`group:${s.group}`));
        return next;
      });
    } catch (e) {
      setListError((e as Error).message);
    }
  }, []);

  useEffect(() => {
    void refreshServers();
  }, [refreshServers, reloadToken]);

  const setFlag = (setter: typeof setLoading, key: string, on: boolean) =>
    setter((prev) => {
      const next = new Set(prev);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });

  const load = useCallback(
    async (server: ServerDto, node: TreeNodeDto): Promise<boolean> => {
      const key = keyOf(server, node);
      setFlag(setLoading, key, true);
      setErrors((e) => {
        const next = { ...e };
        delete next[key];
        return next;
      });
      try {
        for (let attempt = 0; attempt < 3; attempt++) {
          try {
            const nodes = await treeChildren(server.id, node.ref);
            setChildren((c) => ({ ...c, [key]: nodes }));
            setConnected((prev) => new Set(prev).add(server.id));
            return true;
          } catch (e) {
            if (e instanceof NeedsPasswordError) {
              const ok = await requestPassword(server, attempt > 0 ? e.message : undefined);
              if (ok) continue;
              return false;
            }
            throw e;
          }
        }
        return false;
      } catch (e) {
        setErrors((errs) => ({ ...errs, [key]: (e as Error).message }));
        return false;
      } finally {
        setFlag(setLoading, key, false);
      }
    },
    [requestPassword],
  );

  const expand = useCallback(
    async (server: ServerDto, node: TreeNodeDto) => {
      const key = keyOf(server, node);
      setFlag(setExpanded, key, true);
      if (!children[key]) {
        const ok = await load(server, node);
        if (!ok) setFlag(setExpanded, key, false);
      }
    },
    [children, load],
  );

  const collapse = (key: string) => setFlag(setExpanded, key, false);

  const refresh = useCallback(
    async (server: ServerDto, node: TreeNodeDto) => {
      const key = keyOf(server, node);
      // Drop the node's cached subtree (found by walking the cache) and collapse
      // its descendants, so a refresh never shows stale children.
      const dropped = new Set<string>();
      const walk = (k: string) => {
        dropped.add(k);
        (children[k] ?? []).forEach((child) => walk(keyOf(server, child)));
      };
      walk(key);
      setChildren((c) => Object.fromEntries(Object.entries(c).filter(([k]) => !dropped.has(k))));
      setExpanded((prev) => new Set([...prev].filter((k) => k === key || !dropped.has(k))));
      if (expanded.has(key) || node.ref.kind === 'Server') {
        setFlag(setExpanded, key, true);
        await load(server, node);
      }
    },
    [children, expanded, load],
  );

  const disconnect = useCallback(async (server: ServerDto) => {
    await disconnectServer(server.id).catch(() => undefined);
    setChildren((c) => Object.fromEntries(Object.entries(c).filter(([k]) => !k.startsWith(`${server.id}::`))));
    setExpanded((prev) => new Set([...prev].filter((k) => !k.startsWith(`${server.id}::`))));
    setConnected((prev) => {
      const next = new Set(prev);
      next.delete(server.id);
      return next;
    });
  }, []);

  // ---- filtering: a node shows if it matches or has a loaded descendant that does.
  const needle = filter.trim().toLowerCase();
  const matchMemo = useMemo(() => new Map<string, boolean>(), [needle, children]); // eslint-disable-line react-hooks/exhaustive-deps
  const matches = useCallback(
    (server: ServerDto, node: TreeNodeDto): boolean => {
      if (!needle) return true;
      const key = keyOf(server, node);
      const cached = matchMemo.get(key);
      if (cached !== undefined) return cached;
      const self = node.label.toLowerCase().includes(needle);
      const result = self || (children[key] ?? []).some((child) => matches(server, child));
      matchMemo.set(key, result);
      return result;
    },
    [needle, children, matchMemo],
  );

  // ---- flatten the visible tree into rows (drives rendering and keyboard nav).
  const rows = useMemo<Row[]>(() => {
    const out: Row[] = [];
    const groups = [...new Set(servers.map((s) => s.group))];
    for (const group of groups) {
      const groupKey = `group:${group}`;
      out.push({ key: groupKey, depth: 0, kind: 'group', group, parentKey: null, expandable: true });
      if (!expanded.has(groupKey)) continue;
      for (const server of servers.filter((s) => s.group === group)) {
        const root = serverRoot(server);
        if (!matches(server, root)) continue;
        const walk = (node: TreeNodeDto, depth: number, parentKey: string) => {
          const key = keyOf(server, node);
          out.push({ key, depth, kind: 'node', located: { server, node }, parentKey, expandable: node.hasChildren });
          if (!(expanded.has(key) || (needle && children[key]))) return;
          for (const child of children[key] ?? []) {
            if (matches(server, child)) walk(child, depth + 1, key);
          }
        };
        walk(root, 1, groupKey);
      }
    }
    return out;
  }, [servers, expanded, children, matches, needle]);

  const rowByKey = useMemo(() => new Map(rows.map((r) => [r.key, r])), [rows]);

  const select = (row: Row) => {
    setSelected(row.key);
    if (row.located) onSelect(row.located);
  };

  const toggle = (row: Row) => {
    if (row.kind === 'group') {
      setFlag(setExpanded, row.key, !expanded.has(row.key));
      return;
    }
    const { server, node } = row.located!;
    if (expanded.has(row.key)) collapse(row.key);
    else void expand(server, node);
  };

  const activate = (row: Row) => {
    if (row.located && RELATION_KINDS.has(row.located.node.ref.kind)) onOpenData(row.located);
    else if (row.expandable) toggle(row);
  };

  const onKeyDown = (e: KeyboardEvent) => {
    if (!rows.length) return;
    const index = selected ? rows.findIndex((r) => r.key === selected) : -1;
    const current = index >= 0 ? rows[index] : null;
    const move = (i: number) => {
      const target = rows[Math.max(0, Math.min(rows.length - 1, i))];
      select(target);
      containerRef.current?.querySelector(`[data-key="${CSS.escape(target.key)}"]`)?.scrollIntoView({ block: 'nearest' });
    };
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        move(index + 1);
        break;
      case 'ArrowUp':
        e.preventDefault();
        move(index - 1);
        break;
      case 'ArrowRight':
        e.preventDefault();
        if (current?.expandable && !expanded.has(current.key)) toggle(current);
        else if (current && index + 1 < rows.length && rows[index + 1].parentKey === current.key) move(index + 1);
        break;
      case 'ArrowLeft':
        e.preventDefault();
        if (current && expanded.has(current.key)) collapse(current.key);
        else if (current?.parentKey) {
          const parent = rowByKey.get(current.parentKey);
          if (parent) move(rows.indexOf(parent));
        }
        break;
      case 'Enter':
        e.preventDefault();
        if (current) activate(current);
        break;
      case 'F5':
        e.preventDefault();
        if (current?.located) void refresh(current.located.server, current.located.node);
        break;
    }
  };

  const openMenu = (e: MouseEvent, row: Row) => {
    e.preventDefault();
    select(row);
    const items: MenuItem[] = [];
    if (row.kind === 'group') {
      items.push({ label: 'Register Server…', action: onRegister });
      items.push({ label: 'Import Servers (pgAdmin / DataGrip)…', action: onImport });
      items.push({ label: 'Refresh', action: () => void refreshServers() });
    } else {
      const { server, node } = row.located!;
      const kind = node.ref.kind;
      if (kind === 'Server') {
        if (connected.has(server.id)) items.push({ label: 'Disconnect Server', action: () => void disconnect(server) });
        else items.push({ label: 'Connect Server', action: () => void expand(server, node) });
        items.push({ label: 'Query Tool', action: () => onOpenQuery(server, null) });
        items.push({ label: 'Refresh', action: () => void refresh(server, node), separator: true });
        items.push({ label: 'Properties…', action: () => onEditServer(server) });
        items.push({ label: 'Remove Server', action: () => onDeleteServer(server), danger: true });
      } else if (kind === 'Database') {
        items.push({ label: 'Query Tool', action: () => onOpenQuery(server, node.ref.database ?? node.ref.name ?? null) });
        items.push({ label: 'Refresh', action: () => void refresh(server, node), separator: true });
      } else if (RELATION_KINDS.has(kind)) {
        items.push({ label: 'View/Edit Data', action: () => onOpenData(row.located!) });
        items.push({
          label: 'Query Tool',
          action: () => onOpenQuery(server, node.ref.database ?? null, selectTop(server.provider, node.ref.schema ?? null, node.ref.name!)),
        });
        items.push({ label: 'Refresh', action: () => void refresh(server, node), separator: true });
        items.push({ label: 'Copy Name', action: () => void navigator.clipboard?.writeText(node.ref.name ?? node.label) });
      } else {
        items.push({ label: 'Refresh', action: () => void refresh(server, node) });
        if (node.ref.name) items.push({ label: 'Copy Name', action: () => void navigator.clipboard?.writeText(node.ref.name ?? node.label) });
      }
    }
    setMenu({ x: e.clientX, y: e.clientY, items });
  };

  useEffect(() => {
    if (!menu) return;
    const close = () => setMenu(null);
    window.addEventListener('click', close);
    window.addEventListener('blur', close);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('blur', close);
    };
  }, [menu]);

  return (
    <div className="ot">
      <div className="ot__head">
        <span className="ot__title">Object Explorer</span>
        <button className="ot__tool" title="Register server" onClick={onRegister}>
          ＋
        </button>
        <button className="ot__tool" title="Import servers from pgAdmin or DataGrip" onClick={onImport}>
          ⇩
        </button>
        <button
          className="ot__tool"
          title="Refresh selected (F5)"
          onClick={() => {
            const row = selected ? rowByKey.get(selected) : null;
            if (row?.located) void refresh(row.located.server, row.located.node);
            else void refreshServers();
          }}
        >
          ⟳
        </button>
      </div>
      <div className="ot__search">
        <input className="ex-input" placeholder="Filter loaded objects…" value={filter} onChange={(e) => setFilter(e.target.value)} />
      </div>
      {listError ? <div className="ot__error">{listError}</div> : null}
      <div className="ot__body" ref={containerRef} tabIndex={0} onKeyDown={onKeyDown}>
        {servers.length === 0 && !listError ? (
          <div className="ot__empty">
            <p>No servers registered yet.</p>
            <button className="ex-btn" onClick={onRegister}>
              Register Server
            </button>
            <button className="ex-btn ex-btn--ghost" onClick={onImport}>
              Import from pgAdmin / DataGrip
            </button>
          </div>
        ) : null}
        {rows.map((row) => {
          const isOpen = expanded.has(row.key) || Boolean(needle && children[row.key]);
          const isLoading = loading.has(row.key);
          const error = errors[row.key];
          if (row.kind === 'group') {
            const count = servers.filter((s) => s.group === row.group).length;
            return (
              <div
                key={row.key}
                data-key={row.key}
                className={`ot__row ${selected === row.key ? 'ot__row--selected' : ''}`}
                style={{ paddingLeft: 6 }}
                onClick={() => select(row)}
                onDoubleClick={() => toggle(row)}
                onContextMenu={(e) => openMenu(e, row)}
              >
                <Chevron open={isOpen} onClick={() => toggle(row)} />
                <TreeIcon kind="ServerGroup" />
                <span className="ot__label">{row.group}</span>
                <span className="ot__count">({count})</span>
              </div>
            );
          }
          const { server, node } = row.located!;
          const kind = node.ref.kind;
          const iconConnected =
            kind === 'Server' ? connected.has(server.id) : kind === 'Database' ? Boolean(children[row.key]) : true;
          return (
            <div key={row.key}>
              <div
                data-key={row.key}
                className={`ot__row ${selected === row.key ? 'ot__row--selected' : ''}`}
                style={{ paddingLeft: 6 + row.depth * 16 }}
                onClick={() => select(row)}
                onDoubleClick={() => activate(row)}
                onContextMenu={(e) => openMenu(e, row)}
                title={kind === 'Server' ? `${server.provider} · ${server.endpoint}` : undefined}
              >
                {row.expandable ? <Chevron open={isOpen} loading={isLoading} onClick={() => toggle(row)} /> : <span className="ot__chev" />}
                <TreeIcon kind={kind} provider={server.provider} connected={iconConnected} badge={node.badge} />
                <span className="ot__label">{node.label}</span>
                {node.count != null ? <span className="ot__count">({node.count})</span> : null}
                {node.detail ? <span className="ot__detail">{node.detail}</span> : null}
              </div>
              {error ? (
                <div className="ot__node-error" style={{ paddingLeft: 28 + row.depth * 16 }}>
                  {error}
                </div>
              ) : null}
            </div>
          );
        })}
      </div>
      {menu ? (
        <div className="ot__menu" style={{ left: menu.x, top: menu.y }} onClick={(e) => e.stopPropagation()}>
          {menu.items.map((item, i) => (
            <div key={item.label}>
              {item.separator && i > 0 ? <div className="ot__menu-sep" /> : null}
              <button
                className={`ot__menu-item ${item.danger ? 'ot__menu-item--danger' : ''}`}
                onClick={() => {
                  setMenu(null);
                  item.action();
                }}
              >
                {item.label}
              </button>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function Chevron({ open, loading, onClick }: { open: boolean; loading?: boolean; onClick: () => void }) {
  return (
    <span
      className="ot__chev"
      onClick={(e) => {
        e.stopPropagation();
        onClick();
      }}
    >
      {loading ? (
        <span className="ot__spinner" />
      ) : (
        <svg width="10" height="10" viewBox="0 0 10 10" style={{ transform: open ? 'rotate(90deg)' : 'none', transition: 'transform .12s' }}>
          <path d="M3 1.5 7 5 3 8.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      )}
    </span>
  );
}

/** A provider-correct "first 100 rows" query for the Query Tool. */
function selectTop(provider: ServerDto['provider'], schema: string | null, name: string) {
  const quote = (id: string) =>
    provider === 'SqlServer'
      ? `[${id.replace(/]/g, ']]')}]`
      : provider === 'MySql'
        ? `\`${id.replace(/`/g, '``')}\``
        : /^[a-z_][a-z0-9_$]*$/.test(id)
          ? id
          : `"${id.replace(/"/g, '""')}"`;
  const target = schema ? `${quote(schema)}.${quote(name)}` : quote(name);
  return provider === 'SqlServer' ? `SELECT TOP 100 * FROM ${target};` : `SELECT * FROM ${target}\nLIMIT 100;`;
}
