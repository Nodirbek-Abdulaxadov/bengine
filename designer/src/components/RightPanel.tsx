import { useState } from 'react';
import { GeneratePanel } from './GeneratePanel';
import { Inspector } from './Inspector';
import { JsonPreview } from './JsonPreview';
import { SchemaObjects } from './SchemaObjects';

type Tab = 'table' | 'objects' | 'json' | 'generate';

const TABS: { id: Tab; label: string }[] = [
  { id: 'table', label: 'Jadval' },
  { id: 'objects', label: 'Obyektlar' },
  { id: 'json', label: 'JSON' },
  { id: 'generate', label: 'Kod/SQL' },
];

/** Tabbed right dock: table inspector, schema-level objects, live JSON, generation. */
export function RightPanel() {
  const [tab, setTab] = useState<Tab>('table');

  return (
    <div className="rightpanel">
      <div className="tabs">
        {TABS.map((t) => (
          <button key={t.id} className={tab === t.id ? 'tab active' : 'tab'} onClick={() => setTab(t.id)}>
            {t.label}
          </button>
        ))}
      </div>
      <div className="rightpanel__body">
        {tab === 'table' && <Inspector />}
        {tab === 'objects' && <SchemaObjects />}
        {tab === 'json' && <JsonPreview />}
        {tab === 'generate' && <GeneratePanel />}
      </div>
    </div>
  );
}
