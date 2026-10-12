import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  deleteRow,
  exportData,
  fetchData,
  fetchTable,
  insertRow,
  updateRow,
  type DataPageDto,
  type FilterDto,
  type Target,
  type TableStructureDto,
} from '../../api/db';

type SortState = { column: string; direction: 'asc' | 'desc' } | null;

const NUMERIC = new Set(['TinyInt', 'SmallInt', 'Integer', 'BigInt', 'Decimal', 'Real', 'Double']);

function typeLabel(c: TableStructureDto['structure']['columns'][number]) {
  const t = c.type;
  const base = t.dataType.toLowerCase();
  if (t.length) return `${base}(${t.length})`;
  if (t.precision) return `${base}(${t.precision}${t.scale ? `,${t.scale}` : ''})`;
  return base;
}

/** Browse, filter, sort, edit, add, delete, and export the rows of one relation. */
export function DataTab({ target, table }: { target: Target; table: { name: string; schema: string | null } }) {
  const [structure, setStructure] = useState<TableStructureDto['structure'] | null>(null);
  const [data, setData] = useState<DataPageDto | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(100);
  const [sort, setSort] = useState<SortState>(null);
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [applied, setApplied] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [editing, setEditing] = useState<{ row: number; col: number } | null>(null);
  const [editValue, setEditValue] = useState('');
  const [adding, setAdding] = useState(false);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [elapsed, setElapsed] = useState<number | null>(null);

  useEffect(() => {
    fetchTable(target, table.name, table.schema)
      .then((t) => setStructure(t.structure))
      .catch(() => setStructure({ name: table.name, schema: table.schema, columns: [] }));
  }, [target, table.name, table.schema]);

  const typeOf = useMemo(() => new Map((structure?.columns ?? []).map((c) => [c.name, c.type.dataType])), [structure]);
  const typeText = useMemo(() => new Map((structure?.columns ?? []).map((c) => [c.name, typeLabel(c)])), [structure]);
  const pkColumns = structure?.primaryKey?.columnNames ?? [];

  const convert = useCallback(
    (column: string, value: string): unknown => {
      if (value === '') return null;
      if (NUMERIC.has(typeOf.get(column) ?? '')) {
        const n = Number(value);
        return Number.isNaN(n) ? value : n;
      }
      return value;
    },
    [typeOf],
  );

  const filterList = useCallback(
    (source: Record<string, string>): FilterDto[] =>
      Object.entries(source)
        .filter(([, v]) => v !== '')
        .map(([column, v]) =>
          v.toLowerCase() === 'null'
            ? { column, operator: 'IsNull' }
            : NUMERIC.has(typeOf.get(column) ?? '')
              ? { column, operator: 'Equals', value: convert(column, v) }
              : { column, operator: 'Contains', value: v },
        ),
    [typeOf, convert],
  );

  const load = useCallback(async () => {
    setBusy(true);
    setError(null);
    const started = performance.now();
    try {
      const result = await fetchData(target, table.name, table.schema, {
        page,
        pageSize,
        includeTotalCount: true,
        filters: filterList(applied),
        sort: sort ? [sort] : [],
      });
      setData(result);
      setSelected(null);
      setEditing(null);
      setElapsed(performance.now() - started);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }, [target, table.name, table.schema, page, pageSize, sort, applied, filterList]);

  useEffect(() => {
    if (structure) void load();
  }, [structure, load]);

  const columns = data?.columns ?? structure?.columns.map((c) => c.name) ?? [];
  const totalPages = data?.totalCount != null ? Math.max(1, Math.ceil(data.totalCount / pageSize)) : null;

  const toggleSort = (column: string) => {
    setPage(1);
    setSort((s) => (s?.column !== column ? { column, direction: 'asc' } : s.direction === 'asc' ? { column, direction: 'desc' } : null));
  };

  const keyOf = (rowIndex: number): Record<string, unknown> => {
    const row = data!.rows[rowIndex];
    const keyColumns = pkColumns.length > 0 ? pkColumns : columns;
    return Object.fromEntries(keyColumns.map((c) => [c, row[columns.indexOf(c)]]));
  };

  const run = async (work: () => Promise<string>) => {
    setBusy(true);
    setError(null);
    try {
      setNotice(await work());
      await load();
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  const commitEdit = () => {
    if (!editing || !data) return;
    const column = columns[editing.col];
    const current = data.rows[editing.row][editing.col];
    const next = convert(column, editValue);
    const rowIndex = editing.row;
    setEditing(null);
    if (String(current ?? '') === String(next ?? '')) return;
    void run(async () => {
      await updateRow(target, table.name, table.schema, keyOf(rowIndex), { [column]: next });
      return `Updated ${column}.`;
    });
  };

  const removeSelected = () => {
    if (selected === null || !data) return;
    if (!confirm('Delete the selected row? This cannot be undone.')) return;
    void run(async () => {
      const { affected } = await deleteRow(target, table.name, table.schema, keyOf(selected));
      return `Deleted ${affected} row(s).`;
    });
  };

  const saveDraft = () =>
    void run(async () => {
      const values = Object.fromEntries(Object.entries(draft).filter(([, v]) => v !== '').map(([c, v]) => [c, convert(c, v)]));
      await insertRow(target, table.name, table.schema, values);
      setAdding(false);
      setDraft({});
      return 'Row inserted.';
    });

  const download = async (format: 'csv' | 'json' | 'sql') => {
    try {
      const text = await exportData(target, table.name, table.schema, format, {
        pageSize: 1000,
        filters: filterList(applied),
        sort: sort ? [sort] : [],
      });
      const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = `${table.name}.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const applyFilters = () => {
    setPage(1);
    setApplied({ ...filters });
  };

  const from = data && data.rows.length > 0 ? (page - 1) * pageSize + 1 : 0;
  const to = data ? (page - 1) * pageSize + data.rows.length : 0;

  return (
    <div className="ex-tabpanel">
      <div className="ex-toolbar">
        <button className="ex-btn ex-btn--ghost" onClick={() => void load()} disabled={busy} title="Refresh">
          ⟳
        </button>
        <span className="ex-toolbar__sep" />
        <button className="ex-btn ex-btn--ghost" onClick={() => setAdding((a) => !a)} title="Add row">
          ＋ Row
        </button>
        <button className="ex-btn ex-btn--ghost ex-btn--danger-ghost" onClick={removeSelected} disabled={selected === null} title="Delete selected row">
          ✕ Delete
        </button>
        <span className="ex-toolbar__sep" />
        <button className="ex-btn ex-btn--ghost" onClick={applyFilters} title="Apply column filters (Enter)">
          Filter
        </button>
        <button
          className="ex-btn ex-btn--ghost"
          onClick={() => {
            setFilters({});
            setApplied({});
            setPage(1);
          }}
          disabled={Object.keys(applied).length === 0}
        >
          Clear
        </button>
        <div className="ex-toolbar__spacer" />
        <span className="ex-dim">Export</span>
        <button className="ex-btn ex-btn--ghost" onClick={() => void download('csv')}>
          CSV
        </button>
        <button className="ex-btn ex-btn--ghost" onClick={() => void download('json')}>
          JSON
        </button>
        <button className="ex-btn ex-btn--ghost" onClick={() => void download('sql')}>
          SQL
        </button>
      </div>

      {error ? <div className="ex-banner">{error}</div> : null}
      {notice && !error ? <div className="ex-toast">{notice}</div> : null}

      <div className="ex-grid-wrap">
        <table className="ex-grid">
          <thead>
            <tr>
              <th className="ex-rowhead" />
              {columns.map((c) => (
                <th key={c} onClick={() => toggleSort(c)} title="Click to sort">
                  <div className="ex-grid__head">
                    <span>
                      {pkColumns.includes(c) ? <span className="ex-grid__pk">🔑 </span> : null}
                      {c}
                      {sort?.column === c ? <span className="ex-grid__sort">{sort.direction === 'asc' ? ' ▲' : ' ▼'}</span> : null}
                    </span>
                    <span className="ex-grid__type">{typeText.get(c) ?? ''}</span>
                  </div>
                </th>
              ))}
            </tr>
            <tr className="ex-grid__filters">
              <th className="ex-rowhead" />
              {columns.map((c) => (
                <th key={c}>
                  <input
                    className={`ex-grid__filter ${applied[c] ? 'ex-grid__filter--on' : ''}`}
                    value={filters[c] ?? ''}
                    placeholder={NUMERIC.has(typeOf.get(c) ?? '') ? '= value' : 'contains…'}
                    onChange={(e) => setFilters((f) => ({ ...f, [c]: e.target.value }))}
                    onKeyDown={(e) => e.key === 'Enter' && applyFilters()}
                  />
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {adding ? (
              <tr className="ex-row--new">
                <td className="ex-rowhead">＋</td>
                {columns.map((c) => (
                  <td key={c}>
                    <input
                      className="ex-cell-input"
                      value={draft[c] ?? ''}
                      placeholder={typeText.get(c) ?? ''}
                      onChange={(e) => setDraft((d) => ({ ...d, [c]: e.target.value }))}
                      onKeyDown={(e) => e.key === 'Enter' && saveDraft()}
                    />
                  </td>
                ))}
              </tr>
            ) : null}
            {(data?.rows ?? []).map((row, ri) => (
              <tr key={ri} className={selected === ri ? 'ex-row--selected' : undefined}>
                <td className="ex-rowhead" onClick={() => setSelected(ri === selected ? null : ri)}>
                  {(page - 1) * pageSize + ri + 1}
                </td>
                {row.map((cell, ci) => (
                  <td
                    key={ci}
                    className={NUMERIC.has(typeOf.get(columns[ci]) ?? '') ? 'ex-num' : undefined}
                    onClick={() => setSelected(ri)}
                    onDoubleClick={() => {
                      setEditing({ row: ri, col: ci });
                      setEditValue(cell === null || cell === undefined ? '' : String(cell));
                    }}
                    title={cell === null || cell === undefined ? 'NULL' : String(cell)}
                  >
                    {editing && editing.row === ri && editing.col === ci ? (
                      <input
                        autoFocus
                        className="ex-cell-input"
                        value={editValue}
                        onChange={(e) => setEditValue(e.target.value)}
                        onBlur={commitEdit}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter') commitEdit();
                          if (e.key === 'Escape') setEditing(null);
                        }}
                      />
                    ) : cell === null || cell === undefined ? (
                      <span className="ex-null">[null]</span>
                    ) : typeof cell === 'boolean' ? (
                      <span className={cell ? 'ex-true' : 'ex-false'}>{String(cell)}</span>
                    ) : (
                      String(cell)
                    )}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
        {data && data.rows.length === 0 ? <div className="ws__empty">No rows{Object.keys(applied).length ? ' match the filter' : ''}.</div> : null}
      </div>

      <div className="ex-pager">
        {adding ? (
          <>
            <button className="ex-btn" onClick={saveDraft}>
              Save row
            </button>
            <button
              className="ex-btn ex-btn--ghost"
              onClick={() => {
                setAdding(false);
                setDraft({});
              }}
            >
              Cancel
            </button>
          </>
        ) : null}
        <span className="ex-dim">
          {from}–{to}
          {data?.totalCount != null ? ` of ${data.totalCount.toLocaleString()}` : ''} rows
          {elapsed != null ? ` · ${Math.round(elapsed)} ms` : ''}
          {pkColumns.length === 0 && structure ? ' · no primary key: edits match on every column' : ''}
        </span>
        <div className="ex-toolbar__spacer" />
        <span className="ex-dim">Rows/page</span>
        <select
          className="ex-select"
          value={pageSize}
          onChange={(e) => {
            setPageSize(Number(e.target.value));
            setPage(1);
          }}
        >
          {[25, 50, 100, 250, 500, 1000].map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
        <button className="ex-btn ex-btn--ghost" onClick={() => setPage(1)} disabled={busy || page <= 1} title="First page">
          «
        </button>
        <button className="ex-btn ex-btn--ghost" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={busy || page <= 1}>
          ‹ Prev
        </button>
        <span className="ex-dim">
          Page {page}
          {totalPages ? ` / ${totalPages}` : ''}
        </span>
        <button className="ex-btn ex-btn--ghost" onClick={() => setPage((p) => p + 1)} disabled={busy || !data?.hasMore}>
          Next ›
        </button>
        <button
          className="ex-btn ex-btn--ghost"
          onClick={() => totalPages && setPage(totalPages)}
          disabled={busy || !totalPages || page >= totalPages}
          title="Last page"
        >
          »
        </button>
      </div>
    </div>
  );
}
