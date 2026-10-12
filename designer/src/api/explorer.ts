import { postJson, type Provider } from './db';

/** A node's identity: what it is and where it lives. Sent back to load its children or properties. */
export interface NodeRef {
  kind: string;
  database?: string | null;
  schema?: string | null;
  relation?: string | null;
  name?: string | null;
  signature?: string | null;
  isFolder?: boolean;
  id?: string;
}

/** One node of the object explorer tree. */
export interface TreeNodeDto {
  ref: NodeRef;
  label: string;
  detail?: string | null;
  badge?: string | null;
  hasChildren: boolean;
  count?: number | null;
  isFolder: boolean;
  id: string;
}

/** A registered server (the saved password itself is never returned). */
export interface ServerDto {
  id: string;
  name: string;
  group: string;
  provider: Provider;
  host?: string | null;
  port?: number | null;
  maintenanceDatabase?: string | null;
  username?: string | null;
  savePassword: boolean;
  hasPassword: boolean;
  filePath?: string | null;
  options?: string | null;
  comment?: string | null;
  endpoint: string;
  connected: boolean;
}

/** The register/edit server form. */
export interface ServerForm {
  id?: string;
  name: string;
  group: string;
  provider: Provider;
  host: string;
  port: number | null;
  maintenanceDatabase: string;
  username: string;
  password: string;
  savePassword: boolean;
  filePath: string;
  options: string;
  comment: string;
}

/** What the Properties and SQL panels show for a node. */
export interface PropertiesDto {
  title: string;
  properties: { name: string; value: string | null }[];
  sql?: string | null;
  gridColumns: string[];
  gridRows: (string | null)[][];
}

/** A server found in the local pgAdmin configuration. */
export interface PgAdminServer {
  name: string;
  group: string;
  host: string | null;
  port: number | null;
  maintenanceDatabase: string | null;
  username: string | null;
  exists: boolean;
}

async function send<T>(method: string, path: string): Promise<T> {
  const response = await fetch(path, { method });
  if (!response.ok) {
    const detail = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(detail?.error ?? `Request failed (${response.status})`);
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const listServers = () => send<ServerDto[]>('GET', '/api/servers');
export const saveServer = (form: ServerForm) => postJson<ServerDto>('/api/servers', form);
export const deleteServer = (id: string) => send<void>('DELETE', `/api/servers/${id}`);
export const testServer = (form: ServerForm) => postJson<{ ok: boolean }>('/api/servers/test', form);
export const connectServer = (id: string, password?: string, savePassword = false) =>
  postJson<ServerDto>(`/api/servers/${id}/connect`, { password, savePassword });
export const disconnectServer = (id: string) => postJson<{ connected: boolean }>(`/api/servers/${id}/disconnect`, {});
export const pgadminPreview = () =>
  send<{ found: boolean; path?: string; servers: PgAdminServer[] }>('GET', '/api/servers/pgadmin');
export const pgadminImport = () =>
  postJson<{ imported: number; servers: ServerDto[] }>('/api/servers/pgadmin/import', {});
/** A data source found in DataGrip / JetBrains IDE configuration. */
export interface DataGripServer {
  name: string;
  group: string;
  provider: Provider;
  endpoint: string;
  maintenanceDatabase: string | null;
  username: string | null;
  sourceFile: string;
  exists: boolean;
}

const pathQuery = (path?: string) => (path?.trim() ? `?path=${encodeURIComponent(path.trim())}` : '');
export const datagripPreview = (path?: string) =>
  send<{ files: string[]; servers: DataGripServer[]; skipped: { name: string; sourceFile: string; reason: string }[] }>(
    'GET',
    `/api/servers/datagrip${pathQuery(path)}`,
  );
export const datagripImport = (path?: string) =>
  postJson<{ imported: number; servers: ServerDto[] }>(`/api/servers/datagrip/import${pathQuery(path)}`, {});
export const treeChildren = (serverId: string, node: NodeRef) =>
  postJson<TreeNodeDto[]>('/api/tree/children', { serverId, node: stripRef(node) });
export const treeProperties = (serverId: string, node: NodeRef) =>
  postJson<PropertiesDto>('/api/tree/properties', { serverId, node: stripRef(node) });

// The computed fields are output-only; send just the identity.
function stripRef(node: NodeRef): NodeRef {
  const { kind, database, schema, relation, name, signature } = node;
  return { kind, database, schema, relation, name, signature };
}

/** Kinds whose rows can be browsed in a Data tab. */
export const RELATION_KINDS = new Set(['Table', 'View', 'MaterializedView', 'ForeignTable']);
