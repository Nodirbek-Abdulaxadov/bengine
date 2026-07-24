import type { SchemaDocument } from '../schema/contract';

export interface SqlResult {
  provider: string;
  valid: boolean;
  messages: { severity: string | number; code: string; message: string; path: string }[];
  sql: string;
}

export interface CodeResult {
  language: string;
  code: string;
}

export interface ImportSqlResult {
  /** The provider whose dialect the SQL was parsed as. */
  dialect: string;
  /** True when the dialect was auto-detected rather than supplied. */
  detected: boolean;
  /** Number of tables recovered from the SQL. */
  tableCount: number;
  /** The reconstructed schema.json document. */
  schema: SchemaDocument;
}

async function postJson<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    const detail = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(detail?.error ?? `Request failed (${response.status})`);
  }
  return (await response.json()) as T;
}

const post = <T>(path: string, doc: SchemaDocument): Promise<T> => postJson<T>(path, doc);

/** Generates provider DDL from the Bengine backend. */
export const generateSql = (provider: string, doc: SchemaDocument): Promise<SqlResult> =>
  post<SqlResult>(`/api/generate/sql/${provider}`, doc);

/** Generates language models from the Bengine backend. */
export const generateCode = (language: string, doc: SchemaDocument): Promise<CodeResult> =>
  post<CodeResult>(`/api/generate/code/${language}`, doc);

/**
 * Parses raw SQL DDL into a schema.json document via the local Bengine backend.
 * Pass `'auto'` to have the backend detect the dialect, or a provider name to
 * force it.
 */
export const importSql = (sql: string, dialect: string): Promise<ImportSqlResult> =>
  postJson<ImportSqlResult>('/api/import/sql', { sql, dialect });
