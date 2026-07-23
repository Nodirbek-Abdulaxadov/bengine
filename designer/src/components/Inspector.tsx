import type { IndexMethod, IndexType, ReferentialAction } from '../schema/contract';
import { INDEX_METHODS, INDEX_TYPES, REFERENTIAL_ACTIONS } from '../schema/enums';
import { useDesigner, type EditorIndex } from '../state/store';
import { ColumnEditor } from './ColumnEditor';

export function Inspector() {
  const table = useDesigner((s) => s.tables.find((t) => t.id === s.selectedTableId) ?? null);
  const tables = useDesigner((s) => s.tables);
  const foreignKeys = useDesigner((s) => s.foreignKeys);
  const store = useDesigner();

  if (!table) {
    return (
      <aside className="inspector">
        <p className="muted">Bir jadvalni tanlang yoki yangi qo'shing.</p>
      </aside>
    );
  }

  const nameOfTable = (id: string) => tables.find((t) => t.id === id)?.name ?? '?';
  const nameOfColumn = (id: string) =>
    tables.flatMap((t) => t.columns).find((c) => c.id === id)?.name ?? '?';
  const tableFks = foreignKeys.filter((fk) => fk.fromTable === table.id);

  return (
    <aside className="inspector">
      <div className="row">
        <label>Jadval</label>
        <input className="grow" value={table.name} onChange={(e) => store.updateTable(table.id, { name: e.target.value })} />
      </div>
      <div className="row">
        <label>Schema</label>
        <input
          className="grow"
          value={table.schema ?? ''}
          onChange={(e) => store.updateTable(table.id, { schema: e.target.value || undefined })}
        />
      </div>
      <div className="row">
        <label>Comment</label>
        <input
          className="grow"
          value={table.comment ?? ''}
          onChange={(e) => store.updateTable(table.id, { comment: e.target.value || undefined })}
        />
      </div>

      <div className="section">
        <span>Ustunlar</span>
        <button onClick={() => store.addColumn(table.id)}>+ ustun</button>
      </div>
      {table.columns.map((column) => (
        <ColumnEditor key={column.id} tableId={table.id} column={column} />
      ))}

      <div className="section">
        <span>Indekslar</span>
        <button onClick={() => store.addIndex(table.id)}>+ index</button>
      </div>
      {table.indexes.map((index) => (
        <div key={index.id} className="col">
          <div className="row">
            <input className="grow" value={index.name} onChange={(e) => store.updateIndex(table.id, index.id, { name: e.target.value })} />
            <button className="danger" onClick={() => store.removeIndex(table.id, index.id)}>
              ✕
            </button>
          </div>
          <div className="row">
            <select value={index.type} onChange={(e) => store.updateIndex(table.id, index.id, { type: e.target.value as IndexType })}>
              {INDEX_TYPES.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            <select value={index.method} onChange={(e) => store.updateIndex(table.id, index.id, { method: e.target.value as IndexMethod })}>
              {INDEX_METHODS.map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </select>
          </div>
          <div className="index-cols">
            {table.columns.map((c) => {
              const checked = index.columnIds.includes(c.id);
              return (
                <label key={c.id}>
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={(e) => {
                      const columnIds: string[] = e.target.checked
                        ? [...index.columnIds, c.id]
                        : index.columnIds.filter((id) => id !== c.id);
                      store.updateIndex(table.id, index.id, { columnIds } satisfies Partial<EditorIndex>);
                    }}
                  />
                  {c.name}
                </label>
              );
            })}
          </div>
          <div className="row">
            <input
              className="grow"
              placeholder="filter (partial index)"
              value={index.filter ?? ''}
              onChange={(e) => store.updateIndex(table.id, index.id, { filter: e.target.value || undefined })}
            />
          </div>
        </div>
      ))}

      <div className="section">
        <span>Check constraint</span>
        <button onClick={() => store.addCheck(table.id)}>+ check</button>
      </div>
      {table.checks.map((check) => (
        <div key={check.id} className="col">
          <div className="row">
            <input value={check.name} onChange={(e) => store.updateCheck(table.id, check.id, { name: e.target.value })} />
            <button className="danger" onClick={() => store.removeCheck(table.id, check.id)}>
              ✕
            </button>
          </div>
          <div className="row">
            <input
              className="grow"
              placeholder="ifoda, masalan total >= 0"
              value={check.expression}
              onChange={(e) => store.updateCheck(table.id, check.id, { expression: e.target.value })}
            />
          </div>
        </div>
      ))}

      {tableFks.length > 0 && (
        <>
          <div className="section">
            <span>Foreign keys</span>
          </div>
          {tableFks.map((fk) => (
            <div key={fk.id} className="col">
              <div className="row">
                <input className="grow" value={fk.name} onChange={(e) => store.updateForeignKey(fk.id, { name: e.target.value })} />
                <button className="danger" onClick={() => store.removeForeignKey(fk.id)}>
                  ✕
                </button>
              </div>
              <div className="muted small">
                → {nameOfTable(fk.toTable)} ({fk.columnPairs.map((p) => `${nameOfColumn(p.from)}→${nameOfColumn(p.to)}`).join(', ')})
              </div>
              <div className="row">
                <label>onDelete</label>
                <select value={fk.onDelete} onChange={(e) => store.updateForeignKey(fk.id, { onDelete: e.target.value as ReferentialAction })}>
                  {REFERENTIAL_ACTIONS.map((a) => (
                    <option key={a} value={a}>
                      {a}
                    </option>
                  ))}
                </select>
              </div>
              <div className="row">
                <label>onUpdate</label>
                <select value={fk.onUpdate} onChange={(e) => store.updateForeignKey(fk.id, { onUpdate: e.target.value as ReferentialAction })}>
                  {REFERENTIAL_ACTIONS.map((a) => (
                    <option key={a} value={a}>
                      {a}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          ))}
        </>
      )}

      <button className="danger delete" onClick={() => store.removeTable(table.id)}>
        Jadvalni o'chirish
      </button>
    </aside>
  );
}
