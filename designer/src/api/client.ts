import type { SchemaDocument } from '../schema/contract';

export interface SqlResult {
  provider: string;
  valid: boolean;
  messages: { severity: string; code: string; message: string; path: string }[];
  sql: string;
}

export interface CodeResult {
  language: string;
  code: string;
}

async function post<T>(path: string, doc: SchemaDocument): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(doc),
  });
  if (!response.ok) {
    const detail = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(detail?.error ?? `Request failed (${response.status})`);
  }
  return (await response.json()) as T;
}

/** Generates provider DDL from the Bengine backend. */
export const generateSql = (provider: string, doc: SchemaDocument): Promise<SqlResult> =>
  post<SqlResult>(`/api/generate/sql/${provider}`, doc);

/** Generates language models from the Bengine backend. */
export const generateCode = (language: string, doc: SchemaDocument): Promise<CodeResult> =>
  post<CodeResult>(`/api/generate/code/${language}`, doc);
