import { useState } from 'react';
import { generateCode, generateSql } from '../api/client';
import { toSchemaDocument } from '../state/mapping';
import { useDesigner } from '../state/store';

const PROVIDERS = ['PostgreSql', 'SqlServer', 'MySql', 'Sqlite'];
const LANGUAGES = ['CSharp', 'Go', 'Rust', 'TypeScript', 'Python', 'Php'];

/** Calls the Bengine backend to render live SQL / code for the current design. */
export function GeneratePanel() {
  const [provider, setProvider] = useState(PROVIDERS[0]);
  const [language, setLanguage] = useState(LANGUAGES[0]);
  const [output, setOutput] = useState('');
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);

  const run = async (kind: 'sql' | 'code') => {
    setBusy(true);
    setStatus('');
    try {
      const doc = toSchemaDocument(useDesigner.getState());
      if (kind === 'sql') {
        const result = await generateSql(provider, doc);
        setOutput(result.sql);
        setStatus(result.valid ? `✓ valid (${provider})` : `⚠ ${result.messages.length} ta muammo`);
      } else {
        const result = await generateCode(language, doc);
        setOutput(result.code);
        setStatus(`✓ ${language}`);
      }
    } catch (error) {
      setStatus(`✗ ${(error as Error).message}`);
      setOutput('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="generate">
      <div className="row">
        <select value={provider} onChange={(e) => setProvider(e.target.value)}>
          {PROVIDERS.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
        <button disabled={busy} onClick={() => void run('sql')}>
          SQL
        </button>
      </div>
      <div className="row">
        <select value={language} onChange={(e) => setLanguage(e.target.value)}>
          {LANGUAGES.map((l) => (
            <option key={l} value={l}>
              {l}
            </option>
          ))}
        </select>
        <button disabled={busy} onClick={() => void run('code')}>
          Kod
        </button>
      </div>

      <div className="row generate__status">
        <span className="muted small">{busy ? 'generatsiya...' : status}</span>
        {output && <button onClick={() => void navigator.clipboard.writeText(output)}>copy</button>}
      </div>

      <pre className="preview__code">{output}</pre>
    </div>
  );
}
