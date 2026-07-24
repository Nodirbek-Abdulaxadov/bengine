import { useState } from 'react';
import { Inspector } from './Inspector';
import { SchemaObjects } from './SchemaObjects';

type Tab = 'table' | 'objects';

const TABS: { id: Tab; label: string }[] = [
  { id: 'table', label: 'Jadval' },
  { id: 'objects', label: 'Obyektlar' },
];

/** Tabbed right dock: table inspector and schema-level objects. */
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
      </div>
    </div>
  );
}
