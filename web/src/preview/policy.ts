/** Preview 1 is opt-in until the P04 package pins the profile. Unknown profiles fail closed. */
export const workbenchProfile = import.meta.env?.VITE_WORKBENCH_PROFILE ?? 'full';
export const previewEnabled = workbenchProfile !== 'full';
export const previewSupported = workbenchProfile === 'preview-1';
export const previewLimits = Object.freeze({ milliseconds: 30_000, rows: 1000, bytes: 4 * 1024 * 1024, inputBytes: 64 * 1024 });
export const previewTools = ['sql', 'table', 'measurement', 'document', 'kv', 'fulltext', 'vector', 'bucket', 'mq', 'graph'] as const;
export type PreviewTool = typeof previewTools[number];
export function previewToolEnabled(tool: string): boolean { return ['sql', 'table', 'measurement'].includes(tool); }
export const deferredModelReason = '本预览未开放：此模型的专用读取尚未通过服务端资源预算验收。';

export function previewRouteAllowed(name: unknown, query: Record<string, unknown>): boolean {
  if (!previewSupported) return false;
  if (!['home', 'setup', 'login', 'dashboard', 'about', 'sql', 'preview-unavailable'].includes(String(name))) return false;
  if (name !== 'sql') return name === 'login' || Object.keys(query).length === 0;
  return Object.keys(query).every((key) => ['db', 'tool', 'resource'].includes(key))
    && Object.values(query).every((value) => typeof value === 'string')
    && previewTools.includes((query.tool ?? 'sql') as PreviewTool);
}

const sections: Record<string, readonly string[]> = {
  table: ['data'], measurement: ['points', 'schema'],
};
export function previewSectionAllowed(tool: string, section: string): boolean {
  return Boolean(sections[tool]?.includes(section));
}

/** Client admission is deliberately conservative; the Server checks the parsed AST as well. */
export function previewSqlReadAllowed(sql: string): boolean {
  if (new TextEncoder().encode(sql).length > previewLimits.inputBytes) return false;
  // Strip quoted strings/identifiers before checking the single-statement boundary.
  const tokens = sql.replace(/'(?:''|[^'])*'|"(?:""|[^"])*"/gu, 'value').trim().replace(/;\s*$/u, '');
  return !/;|--|\/\*/u.test(tokens)
    && /^(?:SELECT\b|SHOW\s+(?:TABLES|MEASUREMENTS)\s*$|DESC(?:RIBE)?\s+(?:TABLE|MEASUREMENT)\b|EXPLAIN\s+SELECT\b)/iu.test(tokens);
}

export function previewRequestAction(method: string, url: URL, body: Record<string, unknown>, headers: Headers): string | null {
  const path = url.pathname;
  if (method === 'GET' && ['/v1/setup/status', '/v1/db'].includes(path)) return 'session';
  if (method === 'POST' && ['/v1/auth/login', '/v1/setup/initialize'].includes(path)) return 'session';
  const match = /^\/v1\/db\/([^/]+)\/(.+)$/u.exec(path);
  if (!match) return null;
  const route = match[2];
  if (method === 'GET' && ['access', 'schema', 'schema/measurements/revision'].includes(route)) return 'session';
  if (method === 'POST' && route === 'sql') {
    if (headers.get('X-SonnetDB-Workbench-Action') === 'relation.insert.one') return 'relation.insert.one';
    return typeof body.sql === 'string' && previewSqlReadAllowed(body.sql) ? 'sql.read' : null;
  }
  // Scope revision R1 defers unbudgeted dedicated model endpoints, including metadata
  // endpoints that count rows or scan storage. A small response alone is not admission.
  return null;
}
