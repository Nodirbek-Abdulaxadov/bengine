import { useMemo, type ReactNode } from 'react';
import type { PropertiesDto } from '../../api/explorer';

const KEYWORDS = new Set(
  `ADD AFTER ALL ALTER ALWAYS AND AS ASC AUTHORIZATION BEFORE BEGIN BY CACHE CASCADE CASE CAST CHECK COLLATE COLLATION
  COLUMN COMMENT CONNECTION CONSTRAINT CREATE CYCLE DATA DATABASE DECLARE DEFAULT DEFERRABLE DELETE DESC DETERMINISTIC
  DOMAIN DROP EACH ELSE END ENUM EVENT EXECUTE EXISTS EXTENSION FALSE FOR FOREIGN FROM FUNCTION GENERATED GRANT GROUP
  HANDLER IDENTITY IF IMMUTABLE IN INCREMENT INDEX INLINE INSERT INSTEAD INTO IS IS_TEMPLATE JOIN KEY LANGUAGE LC_COLLATE
  LC_CTYPE LEFT LIMIT LOCATION LOGIN MATERIALIZED MAXVALUE MINVALUE MODIFY NO NOT NULL OF OFFSET ON OPTIONS OR ORDER
  OWNER PARALLEL PRIMARY PROCEDURAL PROCEDURE PROVIDER PUBLICATION RANGE REFERENCES REPLACE RESTRICT RETURN RETURNS ROLE
  ROW SAFE SCHEMA SECURITY SELECT SEQUENCE SERVER SET SFUNC STABLE START STORED STYPE SUBSCRIPTION SUBTYPE TABLE TABLES
  TABLESPACE TAG THEN TO TRIGGER TRUE TRUSTED TYPE UNIQUE UPDATE USING VALIDATOR VALUES VERSION VIEW VOLATILE WHEN WHERE
  WITH WITHOUT WRAPPER ENCODING ACTION ASSIGNMENT IMPLICIT INOUT INHERIT NOINHERIT SUPERUSER NOSUPERUSER CREATEDB
  NOCREATEDB CREATEROLE NOCREATEROLE REPLICATION NOREPLICATION BYPASSRLS NOBYPASSRLS NOLOGIN VALID UNTIL LIMIT
  AUTO_INCREMENT ENGINE CHARSET COMMIT ROLLBACK EXPLAIN ANALYZE`.split(/\s+/),
);

const TYPES = new Set(
  `bigint bigserial bit bool boolean bytea char character date datetime decimal double float int int2 int4 int8 integer
  interval json jsonb money nchar numeric nvarchar precision real serial smallint smallserial text time timestamp
  timestamptz tinyint uuid varchar varying xml blob`.split(/\s+/),
);

const TOKEN = /(--[^\n]*|\/\*[\s\S]*?\*\/)|('(?:''|[^'])*')|("(?:""|[^"])*"|`[^`]*`|\[[^\]\n]*\])|(\b\d+(?:\.\d+)?\b)|([A-Za-z_][A-Za-z0-9_$]*)/g;

/** Renders SQL with lightweight syntax highlighting. */
export function SqlView({ sql }: { sql: string }) {
  const parts = useMemo(() => {
    const out: ReactNode[] = [];
    let last = 0;
    let i = 0;
    for (const match of sql.matchAll(TOKEN)) {
      const index = match.index ?? 0;
      if (index > last) out.push(sql.slice(last, index));
      const [text, comment, str, ident, num, word] = match;
      const cls = comment
        ? 'sq-c'
        : str
          ? 'sq-s'
          : ident
            ? 'sq-i'
            : num
              ? 'sq-n'
              : word && KEYWORDS.has(word.toUpperCase())
                ? 'sq-k'
                : word && TYPES.has(word.toLowerCase())
                  ? 'sq-t'
                  : null;
      out.push(cls ? (
        <span key={i++} className={cls}>
          {text}
        </span>
      ) : (
        text
      ));
      last = index + text.length;
    }
    if (last < sql.length) out.push(sql.slice(last));
    return out;
  }, [sql]);

  return <pre className="ex-code sq">{parts}</pre>;
}

/** The Properties tab: a property grid for an object, or a children grid for a collection. */
export function PropertiesPanel({ data }: { data: PropertiesDto }) {
  if (data.gridColumns.length > 0) {
    const keep = data.gridColumns.map((_, i) => i === 0 || data.gridRows.some((row) => row[i]));
    return (
      <div className="ws__scroll">
        <table className="pp">
          <thead>
            <tr>
              {data.gridColumns.map((c, i) => (keep[i] ? <th key={c}>{c}</th> : null))}
            </tr>
          </thead>
          <tbody>
            {data.gridRows.map((row, i) => (
              <tr key={i}>
                {row.map((cell, j) =>
                  keep[j] ? (
                    <td key={j} className={j === 0 ? 'pp__name' : undefined}>
                      {cell ?? ''}
                    </td>
                  ) : null,
                )}
              </tr>
            ))}
          </tbody>
        </table>
        {data.gridRows.length === 0 ? <div className="ws__empty">No objects.</div> : null}
      </div>
    );
  }

  return (
    <div className="ws__scroll">
      <table className="pp pp--kv">
        <tbody>
          {data.properties.map((p) => (
            <tr key={p.name}>
              <th>{p.name}</th>
              <td>
                {p.value === 'Yes' ? (
                  <span className="pp__bool pp__bool--yes">Yes</span>
                ) : p.value === 'No' ? (
                  <span className="pp__bool">No</span>
                ) : (
                  p.value
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {data.properties.length === 0 ? <div className="ws__empty">No properties.</div> : null}
    </div>
  );
}
