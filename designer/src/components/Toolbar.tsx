import { useRef, useState } from 'react';
import type { SchemaDocument } from '../schema/contract';
import { redo, undo, useHistory } from '../state/history';
import { fromSchemaDocument } from '../state/mapping';
import { useDesigner } from '../state/store';
import { toast } from '../state/toast';
import { ExportMenu } from './ExportMenu';
import { ImportSqlDialog } from './ImportSqlDialog';

/** Top bar: schema name, add-table, undo/redo, import, and the export menu. */
export function Toolbar() {
  const fileInput = useRef<HTMLInputElement>(null);
  const schemaName = useDesigner((s) => s.schemaName);
  const { setSchemaName, addTable, replaceAll } = useDesigner();
  const { canUndo, canRedo } = useHistory();
  const [sqlImportOpen, setSqlImportOpen] = useState(false);

  const importJson = async (file: File) => {
    try {
      const text = await file.text();
      const doc = JSON.parse(text) as SchemaDocument;
      replaceAll(fromSchemaDocument(doc));
      toast.success(`${file.name} yuklandi`);
    } catch (error) {
      toast.error('schema.json o‘qilmadi', [(error as Error).message]);
    }
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

      <button onClick={() => fileInput.current?.click()}>Import JSON</button>
      <button onClick={() => setSqlImportOpen(true)}>Import SQL</button>
      <ExportMenu />

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
      {sqlImportOpen && <ImportSqlDialog onClose={() => setSqlImportOpen(false)} />}
    </header>
  );
}
