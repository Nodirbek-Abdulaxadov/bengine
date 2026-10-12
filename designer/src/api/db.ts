/** The four providers the Bengine engine supports. */
export type Provider = 'PostgreSql' | 'SqlServer' | 'MySql' | 'Sqlite';

/**
 * Where a request goes: a registered server and one of its databases, or a raw
 * provider and connection string. Spread into every request body.
 */
export type Target =
  | { serverId: string; database?: string | null }
  | { provider: Provider; connectionString: string };

/** One page of rows read from a relation. */
export interface DataPageDto {
  columns: string[];
  columnTypes: string[];
  rows: unknown[][];
  page: number;
  pageSize: number;
  totalCount: number | null;
  hasMore: boolean;
}

/** A single column filter, mirroring the backend's FilterCondition. */
export interface FilterDto {
  column: string;
  operator: string;
  value?: unknown;
  values?: unknown[];
}

/** A single sort term. */
export interface SortDto {
  column: string;
  direction?: 'asc' | 'desc';
}

/** The paging, filter, and sort options for a data read. */
export interface DataQueryDto {
  page?: number;
  pageSize?: number;
  filters?: FilterDto[];
  sort?: SortDto[];
  includeTotalCount?: boolean;
}

/** A column as reported by the structure endpoint (a slice of the AST). */
export interface ColumnDto {
  name: string;
  type: { dataType: string; length?: number; precision?: number; scale?: number };
  nullability: string;
}

/** The structure of one relation plus its rendered DDL. */
export interface TableStructureDto {
  structure: {
    name: string;
    schema: string | null;
    columns: ColumnDto[];
    primaryKey?: { name: string; columnNames: string[] } | null;
  };
  ddl: string;
}

/** The outcome of an ad-hoc SQL statement. */
export interface QueryResultDto {
  isQuery: boolean;
  columns: string[];
  columnTypes: string[];
  rows: unknown[][];
  rowsAffected: number;
  truncated: boolean;
  elapsedMilliseconds: number;
}

/** Thrown when the server refused the login, so the caller can prompt for a password. */
export class NeedsPasswordError extends Error {}

/** POSTs JSON and returns the parsed response, raising the backend's error message on failure. */
export async function postJson<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    const detail = (await response.json().catch(() => null)) as { error?: string; needsPassword?: boolean } | null;
    const message = detail?.error ?? `Request failed (${response.status})`;
    throw detail?.needsPassword ? new NeedsPasswordError(message) : new Error(message);
  }
  return (await response.json()) as T;
}

/** Reads one page of rows from a relation. */
export const fetchData = (target: Target, table: string, schema: string | null, query: DataQueryDto) =>
  postJson<DataPageDto>('/api/db/data', { ...target, table, schema, query });

/** Reads one relation's structure (columns, primary key) and DDL. */
export const fetchTable = (target: Target, table: string, schema: string | null) =>
  postJson<TableStructureDto>('/api/db/table', { ...target, table, schema });

/** Runs an ad-hoc SQL statement. */
export const runQuery = (target: Target, sql: string, maxRows = 1000) =>
  postJson<QueryResultDto>('/api/db/query', { ...target, sql, maxRows });

/** Inserts one row. */
export const insertRow = (target: Target, table: string, schema: string | null, values: Record<string, unknown>) =>
  postJson<{ affected: number }>('/api/db/row/insert', { ...target, table, schema, values });

/** Updates the row matching a key. */
export const updateRow = (
  target: Target,
  table: string,
  schema: string | null,
  key: Record<string, unknown>,
  values: Record<string, unknown>,
) => postJson<{ affected: number }>('/api/db/row/update', { ...target, table, schema, key, values });

/** Deletes the row matching a key. */
export const deleteRow = (target: Target, table: string, schema: string | null, key: Record<string, unknown>) =>
  postJson<{ affected: number }>('/api/db/row/delete', { ...target, table, schema, key });

/** Exports the (filtered, sorted) rows of a relation as csv, json, or sql text. */
export async function exportData(
  target: Target,
  table: string,
  schema: string | null,
  format: 'csv' | 'json' | 'sql',
  query: DataQueryDto,
): Promise<string> {
  const response = await fetch('/api/db/export', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...target, table, schema, format, query }),
  });
  if (!response.ok) {
    const detail = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(detail?.error ?? `Export failed (${response.status})`);
  }
  return response.text();
}

const NUMERIC_TYPE = /^(smallint|integer|bigint|int\d?|tinyint|mediumint|numeric|decimal|real|double|float\d?|money|smallmoney|serial|bigserial)/i;

/** Whether a database type name is numeric, so its cells right-align. */
export const isNumericType = (typeName: string | undefined) => !!typeName && NUMERIC_TYPE.test(typeName.trim());
