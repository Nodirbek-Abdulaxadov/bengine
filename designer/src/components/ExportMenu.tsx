import { useEffect, useRef, useState } from 'react';
import { generateCode, generateSql } from '../api/client';
import { exportDiagramPdf } from '../export/diagramPdf';
import { toSchemaDocument } from '../state/mapping';
import { useDesigner } from '../state/store';
import { toast } from '../state/toast';

const PROVIDERS: { value: string; label: string }[] = [
  { value: 'PostgreSql', label: 'PostgreSQL' },
  { value: 'MySql', label: 'MySQL' },
  { value: 'SqlServer', label: 'SqlServer' },
  { value: 'Sqlite', label: 'Sqlite' },
  { value: 'Oracle', label: 'Oracle' },
];

const LANGUAGES: { value: string; label: string; ext: string }[] = [
  { value: 'CSharp', label: 'C#', ext: 'cs' },
  { value: 'Go', label: 'Go', ext: 'go' },
  { value: 'Python', label: 'Python', ext: 'py' },
  { value: 'Rust', label: 'Rust', ext: 'rs' },
  { value: 'TypeScript', label: 'TypeScript', ext: 'ts' },
  { value: 'Php', label: 'PHP', ext: 'php' },
];

function download(filename: string, content: string, mime: string): void {
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

type Sub = 'sql' | 'code' | null;

/** Single Export dropdown: schema.json, SQL per provider, and code per language. */
export function ExportMenu() {
  const [open, setOpen] = useState(false);
  const [sub, setSub] = useState<Sub>(null);
  const [busy, setBusy] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const schemaName = useDesigner((s) => s.schemaName);

  useEffect(() => {
    if (!open) {
      return;
    }
    const onDown = (e: MouseEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) {
        setOpen(false);
        setSub(null);
      }
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  const close = () => {
    setOpen(false);
    setSub(null);
  };

  const exportJson = () => {
    const name = `${schemaName}.schema.json`;
    const doc = toSchemaDocument(useDesigner.getState());
    download(name, JSON.stringify(doc, null, 2), 'application/json');
    toast.success(`${name} yuklandi`);
    close();
  };

  const exportPdf = async () => {
    setBusy(true);
    try {
      await exportDiagramPdf();
      toast.success('Diagramma PDF yuklandi');
    } catch (error) {
      toast.error('PDF chiqmadi', [(error as Error).message]);
    } finally {
      setBusy(false);
      close();
    }
  };

  const exportSql = async (provider: string) => {
    setBusy(true);
    try {
      const doc = toSchemaDocument(useDesigner.getState());
      const result = await generateSql(provider, doc);
      if (!result.valid) {
        toast.error(
          `Sxemada ${result.messages.length} ta muammo`,
          result.messages.map((m) => `${m.path || m.code}: ${m.message}`),
        );
        return;
      }
      const name = `${schemaName}.${provider.toLowerCase()}.sql`;
      download(name, result.sql, 'text/plain');
      toast.success(`${name} yuklandi`);
    } catch (error) {
      toast.error('SQL generatsiya qilinmadi', [(error as Error).message]);
    } finally {
      setBusy(false);
      close();
    }
  };

  const exportCode = async (language: string, ext: string) => {
    setBusy(true);
    try {
      const doc = toSchemaDocument(useDesigner.getState());
      const result = await generateCode(language, doc);
      const name = `${schemaName}.${ext}`;
      download(name, result.code, 'text/plain');
      toast.success(`${name} yuklandi`);
    } catch (error) {
      toast.error('Kod generatsiya qilinmadi', [(error as Error).message]);
    } finally {
      setBusy(false);
      close();
    }
  };

  return (
    <div className="menu" ref={root}>
      <button className="primary" onClick={() => setOpen((v) => !v)}>
        Export ▾
      </button>

      {open && (
        <div className="menu__panel">
          <button className="menu__item" onMouseEnter={() => setSub(null)} onClick={exportJson}>
            Schema.json
          </button>

          <button
            className="menu__item"
            disabled={busy}
            onMouseEnter={() => setSub(null)}
            onClick={() => void exportPdf()}
          >
            PDF (diagramma)
          </button>

          <div className="menu__sub" onMouseEnter={() => setSub('sql')}>
            <button className="menu__item">
              SQL <span className="menu__arrow">▸</span>
            </button>
            {sub === 'sql' && (
              <div className="menu__flyout">
                {PROVIDERS.map((p) => (
                  <button
                    key={p.value}
                    className="menu__item"
                    disabled={busy}
                    onClick={() => void exportSql(p.value)}
                  >
                    {p.label}
                  </button>
                ))}
              </div>
            )}
          </div>

          <div className="menu__sub" onMouseEnter={() => setSub('code')}>
            <button className="menu__item">
              Kod <span className="menu__arrow">▸</span>
            </button>
            {sub === 'code' && (
              <div className="menu__flyout">
                {LANGUAGES.map((l) => (
                  <button
                    key={l.value}
                    className="menu__item"
                    disabled={busy}
                    onClick={() => void exportCode(l.value, l.ext)}
                  >
                    {l.label}
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
