import type { ReactNode } from 'react';
import type { Provider } from '../../api/db';

const PROVIDER_COLOR: Record<Provider, string> = {
  PostgreSql: '#5b9bd5',
  MySql: '#f29111',
  SqlServer: '#e5484d',
  Sqlite: '#4fc1e9',
};

/** Maps a folder kind to the item kind whose icon it shows (TablesFolder → Table). */
export function itemKindOf(kind: string): string {
  if (!kind.endsWith('Folder')) return kind;
  const base = kind.slice(0, -'Folder'.length);
  if (base === 'Roles') return 'GroupRole';
  if (base === 'Constraints') return 'Constraint';
  if (base.endsWith('Indexes')) return 'Index';
  return base.endsWith('s') ? base.slice(0, -1) : base;
}

const Svg = ({ children }: { children: ReactNode }) => (
  <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true" style={{ flexShrink: 0, display: 'block' }}>
    {children}
  </svg>
);

const Grid = ({ color }: { color: string }) => (
  <Svg>
    <rect x="1.5" y="2.5" width="13" height="11" rx="1.6" fill="#17202d" stroke={color} />
    <rect x="1.5" y="2.5" width="13" height="3.2" rx="1.6" fill={color} />
    <path d="M1.8 9.3h12.4M5.9 5.7v7.6M10.1 5.7v7.6" stroke={color} strokeWidth="0.9" opacity="0.75" />
  </Svg>
);

const Glyph = ({ text, color, size = 8 }: { text: string; color: string; size?: number }) => (
  <Svg>
    <rect x="1" y="2" width="14" height="12" rx="3" fill={color} />
    <text
      x="8"
      y="8.4"
      textAnchor="middle"
      dominantBaseline="middle"
      fontSize={size}
      fontWeight="700"
      fontFamily="ui-sans-serif, system-ui, sans-serif"
      fill="#0e131b"
    >
      {text}
    </text>
  </Svg>
);

const Key = ({ color }: { color: string }) => (
  <Svg>
    <circle cx="5" cy="8" r="3" fill="none" stroke={color} strokeWidth="1.9" />
    <path d="M8 8h6.3M11.8 8v2.6M14 8v2.1" stroke={color} strokeWidth="1.9" strokeLinecap="round" />
  </Svg>
);

const Rack = ({ color }: { color: string }) => (
  <>
    <rect x="1.5" y="1.8" width="13" height="5.2" rx="1.2" fill={color} />
    <rect x="1.5" y="9" width="13" height="5.2" rx="1.2" fill={color} />
    <circle cx="4.2" cy="4.4" r="0.95" fill="#0e131b" />
    <circle cx="4.2" cy="11.6" r="0.95" fill="#0e131b" />
    <path d="M7 4.4h5.2M7 11.6h5.2" stroke="#0e131b" strokeWidth="1.1" opacity="0.55" />
  </>
);

/** A 16×16 icon for an object-explorer node kind. */
export function TreeIcon({
  kind,
  provider = 'PostgreSql',
  connected = true,
  badge,
}: {
  kind: string;
  provider?: Provider;
  connected?: boolean;
  badge?: string | null;
}) {
  switch (itemKindOf(kind)) {
    case 'ServerGroup':
      return (
        <Svg>
          <rect x="1" y="2" width="9" height="4" rx="1" fill="#8a9bb3" />
          <rect x="1" y="7" width="9" height="4" rx="1" fill="#8a9bb3" />
          <rect x="6" y="9" width="9" height="5" rx="1" fill="#b6c3d6" />
        </Svg>
      );
    case 'Server':
      return (
        <Svg>
          <Rack color={connected ? PROVIDER_COLOR[provider] : '#5b6576'} />
          {!connected ? (
            <>
              <circle cx="12.4" cy="12.4" r="3.3" fill="#e5484d" stroke="#151b26" strokeWidth="1" />
              <path d="M11 11l2.8 2.8M13.8 11 11 13.8" stroke="#fff" strokeWidth="1.2" strokeLinecap="round" />
            </>
          ) : null}
        </Svg>
      );
    case 'Database':
      return (
        <Svg>
          <path
            d="M2.5 4v8c0 1.2 2.5 2.2 5.5 2.2s5.5-1 5.5-2.2V4"
            fill={connected ? '#c9973a' : '#4f5868'}
          />
          <ellipse cx="8" cy="4" rx="5.5" ry="2.2" fill={connected ? '#f0c060' : '#6b7686'} />
          <path d="M2.5 8.1c0 1.2 2.5 2.2 5.5 2.2s5.5-1 5.5-2.2" fill="none" stroke={connected ? '#f0c060' : '#6b7686'} strokeWidth="0.9" />
        </Svg>
      );
    case 'Schema':
      return (
        <Svg>
          <path d="M8 1.5 14.5 8 8 14.5 1.5 8z" fill="#e06c75" />
          <path d="M8 5 11 8 8 11 5 8z" fill="#f4a4ab" />
        </Svg>
      );
    case 'Catalog':
      return (
        <Svg>
          <path d="M8 1.5 14.5 8 8 14.5 1.5 8z" fill="#b48ead" />
          <path d="M8 5 11 8 8 11 5 8z" fill="#d8bfd8" />
        </Svg>
      );
    case 'Table':
      return <Grid color="#6ea8fe" />;
    case 'View':
      return <Grid color="#5fd38d" />;
    case 'MaterializedView':
      return <Grid color="#c792ea" />;
    case 'ForeignTable':
      return <Grid color="#f0a35e" />;
    case 'Column':
      return (
        <Svg>
          <rect x="4.5" y="1.5" width="7" height="13" rx="1.4" fill="#17202d" stroke="#8fb3d9" />
          <rect x="4.5" y="1.5" width="7" height="3.2" rx="1.4" fill="#8fb3d9" />
          {badge === 'PK' ? <circle cx="12.6" cy="12.6" r="2.6" fill="#e5c07b" stroke="#151b26" /> : null}
          {badge === 'FK' ? <circle cx="12.6" cy="12.6" r="2.6" fill="#61afef" stroke="#151b26" /> : null}
        </Svg>
      );
    case 'Constraint':
      return <Key color="#b9a26b" />;
    case 'PrimaryKey':
      return <Key color="#e5c07b" />;
    case 'ForeignKey':
      return <Key color="#61afef" />;
    case 'UniqueConstraint':
      return <Glyph text="U" color="#56b6c2" />;
    case 'CheckConstraint':
      return (
        <Svg>
          <path d="M2.5 8.5l3.5 3.5 7.5-8" stroke="#98c379" strokeWidth="2.2" fill="none" strokeLinecap="round" strokeLinejoin="round" />
        </Svg>
      );
    case 'ExclusionConstraint':
      return (
        <Svg>
          <circle cx="8" cy="8" r="5.5" fill="none" stroke="#e06c75" strokeWidth="1.8" />
          <path d="M4.2 11.8 11.8 4.2" stroke="#e06c75" strokeWidth="1.8" />
        </Svg>
      );
    case 'Index':
      return (
        <Svg>
          <path d="M4 1.5h8v13l-4-3.2-4 3.2z" fill="#a78bfa" />
        </Svg>
      );
    case 'Trigger':
      return (
        <Svg>
          <path d="M9.5 1 3 9.2h4.3L6.3 15 13 6.6H8.6z" fill="#f5a623" />
        </Svg>
      );
    case 'Function':
      return <Glyph text="fx" color="#3fb6a8" />;
    case 'TriggerFunction':
      return <Glyph text="fx" color="#f5a623" />;
    case 'Procedure':
      return <Glyph text="P" color="#61afef" />;
    case 'Aggregate':
      return <Glyph text="Σ" color="#d19a66" size={9} />;
    case 'Sequence':
      return <Glyph text="123" color="#c678dd" size={6} />;
    case 'Type':
      return <Glyph text="T" color="#56b6c2" />;
    case 'Domain':
      return <Glyph text="D" color="#98c379" />;
    case 'Collation':
      return <Glyph text="Aa" color="#abb2bf" size={7} />;
    case 'Language':
      return <Glyph text="{}" color="#e5c07b" />;
    case 'Event':
      return (
        <Svg>
          <circle cx="8" cy="8" r="6" fill="none" stroke="#e0af68" strokeWidth="1.7" />
          <path d="M8 4.5V8l2.5 1.6" stroke="#e0af68" strokeWidth="1.7" fill="none" strokeLinecap="round" />
        </Svg>
      );
    case 'Extension':
      return (
        <Svg>
          <path d="M2.5 5.5h3a1.6 1.6 0 1 1 3.2 0h3v3a1.6 1.6 0 1 0 0 3.2v3h-9.2z" fill="#8bc34a" />
        </Svg>
      );
    case 'Cast':
      return (
        <Svg>
          <path d="M2 5.2h10.5M10 2.7l2.5 2.5L10 7.7M14 10.8H3.5M6 8.3l-2.5 2.5L6 13.3" stroke="#26a69a" strokeWidth="1.6" fill="none" strokeLinecap="round" strokeLinejoin="round" />
        </Svg>
      );
    case 'EventTrigger':
      return (
        <Svg>
          <circle cx="8" cy="8" r="6.3" fill="none" stroke="#f78c6c" strokeWidth="1.4" />
          <path d="M8.8 3.6 5.2 8.6h2.5l-.6 3.8 3.7-5.1H8.3z" fill="#f78c6c" />
        </Svg>
      );
    case 'ForeignDataWrapper':
      return (
        <Svg>
          <path d="M5.5 1.5v4M10.5 1.5v4" stroke="#ff9e64" strokeWidth="1.6" strokeLinecap="round" />
          <path d="M3.5 5.5h9v2.2a4.5 4.5 0 0 1-9 0z" fill="#ff9e64" />
          <path d="M8 12.2v2.5" stroke="#ff9e64" strokeWidth="1.6" strokeLinecap="round" />
        </Svg>
      );
    case 'ForeignServer':
      return (
        <Svg>
          <Rack color="#ff9e64" />
        </Svg>
      );
    case 'Publication':
      return (
        <Svg>
          <circle cx="8" cy="9" r="1.8" fill="#7dcfff" />
          <path d="M4.8 12.2a4.5 4.5 0 0 1 0-6.4M11.2 5.8a4.5 4.5 0 0 1 0 6.4M2.6 14.4a7.6 7.6 0 0 1 0-10.8M13.4 3.6a7.6 7.6 0 0 1 0 10.8" stroke="#7dcfff" strokeWidth="1.3" fill="none" strokeLinecap="round" />
        </Svg>
      );
    case 'Subscription':
      return (
        <Svg>
          <path d="M1.5 9.5h4l1 2h3l1-2h4v4.5h-13z" fill="#7dcfff" />
          <path d="M8 1.5v6.5M5.5 5.5 8 8l2.5-2.5" stroke="#7dcfff" strokeWidth="1.6" fill="none" strokeLinecap="round" strokeLinejoin="round" />
        </Svg>
      );
    case 'LoginRole':
      return (
        <Svg>
          <circle cx="8" cy="5" r="3" fill="#61afef" />
          <path d="M2.5 14.5a5.5 5 0 0 1 11 0z" fill="#61afef" />
        </Svg>
      );
    case 'GroupRole':
      return (
        <Svg>
          <circle cx="5.5" cy="5.5" r="2.4" fill="#9aa5ce" />
          <circle cx="11" cy="5" r="2.4" fill="#c0caf5" />
          <path d="M1 14.5a4.5 4.2 0 0 1 9 0zM6.5 14a4.5 4.2 0 0 1 9 0z" fill="#9aa5ce" />
        </Svg>
      );
    case 'Tablespace':
      return (
        <Svg>
          <path d="M8 2 14.5 5 8 8 1.5 5z" fill="#a9b1d6" />
          <path d="M1.5 8 8 11l6.5-3M1.5 11 8 14l6.5-3" stroke="#a9b1d6" strokeWidth="1.4" fill="none" strokeLinejoin="round" />
        </Svg>
      );
    default:
      return (
        <Svg>
          <rect x="2.5" y="2.5" width="11" height="11" rx="2" fill="#5b6576" />
        </Svg>
      );
  }
}
