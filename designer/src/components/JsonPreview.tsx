import { useMemo } from 'react';
import { toSchemaDocument } from '../state/mapping';
import { useDesigner } from '../state/store';

/** Live, read-only view of the schema.json the current design produces. */
export function JsonPreview() {
  // Select each slice separately so every selector returns a stable reference;
  // returning a fresh object from one selector would loop under zustand v5.
  const schemaName = useDesigner((s) => s.schemaName);
  const tables = useDesigner((s) => s.tables);
  const foreignKeys = useDesigner((s) => s.foreignKeys);
  const views = useDesigner((s) => s.views);
  const sequences = useDesigner((s) => s.sequences);
  const triggers = useDesigner((s) => s.triggers);

  const json = useMemo(
    () =>
      JSON.stringify(
        toSchemaDocument({ schemaName, tables, foreignKeys, views, sequences, triggers }),
        null,
        2,
      ),
    [schemaName, tables, foreignKeys, views, sequences, triggers],
  );

  return (
    <div className="preview">
      <div className="section">
        <span>schema.json</span>
        <button onClick={() => void navigator.clipboard.writeText(json)}>copy</button>
      </div>
      <pre className="preview__code">{json}</pre>
    </div>
  );
}
