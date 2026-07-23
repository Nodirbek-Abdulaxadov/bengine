import { useRef } from 'react';
import type { SchemaDocument } from '../schema/contract';
import { redo, undo, useHistory } from '../state/history';
import { fromSchemaDocument, toSchemaDocument } from '../state/mapping';
import { useDesigner } from '../state/store';

/** Top bar: schema name, add-table, and schema.json import / export. */
export function Toolbar() {
  const fileInput = useRef<HTMLInputElement>(null);
  const schemaName = useDesigner((s) => s.schemaName);
  const { setSchemaName, addTable, replaceAll } = useDesigner();
  const { canUndo, canRedo } = useHistory();

  const exportJson = () => {
    const doc = toSchemaDocument(useDesigner.getState());
    const blob = new Blob([JSON.stringify(doc, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = 'schema.json';
    link.click();
    URL.revokeObjectURL(url);
  };

  const importJson = async (file: File) => {
    const text = await file.text();
    const doc = JSON.parse(text) as SchemaDocument;
    replaceAll(fromSchemaDocument(doc));
  };

  return (
    <header className="toolbar">
      <strong className="toolbar__brand">Bengine Designer</strong>
      <input
        className="toolbar__name"
        value={schemaName}
        onChange={(e) => setSchemaName(e.target.value)}
        placeholder="schema nomi"
      />
      <button onClick={addTable}>+ Jadval</button>
      <button onClick={undo} disabled={!canUndo} title="Undo">
        ↶
      </button>
      <button onClick={redo} disabled={!canRedo} title="Redo">
        ↷
      </button>
      <div className="toolbar__spacer" />
      <button onClick={() => fileInput.current?.click()}>Import</button>
      <button className="primary" onClick={exportJson}>
        Export schema.json
      </button>
      <input
        ref={fileInput}
        type="file"
        accept="application/json"
        hidden
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file) {
            void importJson(file);
          }
          e.target.value = '';
        }}
      />
    </header>
  );
}
