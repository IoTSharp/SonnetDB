import type { AxiosInstance } from 'axios';

/** SQL ndjson 流的 meta 行（首行）。 */
export interface SqlMeta {
  type: 'meta';
  columns: string[];
}

/** SQL ndjson 流的 end 行（末行）。 */
export interface SqlEnd {
  type: 'end';
  rowCount: number;
  recordsAffected: number;
  elapsedMs: number;
}

/** SQL ndjson 流的 error 行（异常时附加）。 */
export interface SqlError {
  type?: 'error';
  code?: string;
  message: string;
}

/** SQL 执行的完整结果（前端 SqlConsole 直接展示）。 */
export interface SqlResultSet {
  columns: string[];
  rows: unknown[][];
  end: SqlEnd | null;
  error: SqlError | null;
  /** 是否被识别为 SELECT/SHOW（含 columns）。 */
  hasColumns: boolean;
}

/**
 * 把后端 ndjson 响应解析成结构化 SqlResultSet。
 * - 第一行为 meta（{type:"meta",columns:[...]}）。
 * - 中间每行为 JSON 数组，按 meta.columns 顺序对应。
 * - 末行为 end 或 error。
 */
export function parseNdjson(body: string): SqlResultSet {
  const result: SqlResultSet = { columns: [], rows: [], end: null, error: null, hasColumns: false };
  const lines = body.split(/\r?\n/).filter((l) => l.length > 0);
  for (const line of lines) {
    let obj: unknown;
    try {
      obj = JSON.parse(line);
    } catch {
      continue;
    }
    if (Array.isArray(obj)) {
      result.rows.push(obj);
      continue;
    }
    if (obj && typeof obj === 'object') {
      const o = obj as Record<string, unknown>;
      if (o.type === 'meta' && Array.isArray(o.columns)) {
        result.columns = o.columns as string[];
        result.hasColumns = true;
      } else if (o.type === 'end') {
        result.end = {
          type: 'end',
          rowCount: typeof o.rowCount === 'number' ? o.rowCount : 0,
          recordsAffected: typeof o.recordsAffected === 'number' ? o.recordsAffected : -1,
          elapsedMs: typeof o.elapsedMs === 'number'
            ? o.elapsedMs
            : typeof o.elapsedMilliseconds === 'number'
              ? o.elapsedMilliseconds
              : 0,
        };
      } else if (typeof o.message === 'string' && (o.code || o.type === 'error')) {
        result.error = o as unknown as SqlError;
      }
    }
  }
  return result;
}

/**
 * 执行控制面 SQL（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS / SHOW DATABASES 等）。
 * 走服务端 <c>POST /v1/sql</c> 端点（admin only）。
 */
export async function execControlPlaneSql(api: AxiosInstance, sql: string): Promise<SqlResultSet> {
  return doExec(api, '/v1/sql', sql);
}

/**
 * 执行数据面 SQL（INSERT / SELECT / DELETE / CREATE MEASUREMENT 等）。
 */
export async function execDataSql(api: AxiosInstance, db: string, sql: string): Promise<SqlResultSet> {
  return doExec(api, `/v1/db/${encodeURIComponent(db)}/sql`, sql);
}

async function doExec(api: AxiosInstance, url: string, sql: string): Promise<SqlResultSet> {
  const resp = await api.post(url, { sql }, {
    responseType: 'text',
    transformResponse: (v) => v,
    validateStatus: () => true,
  });
  const ct = resp.headers['content-type']?.toString() ?? '';
  if (typeof resp.data === 'string' && ct.includes('ndjson')) {
    return parseNdjson(resp.data);
  }
  // 非 ndjson → JSON 错误体（{code, message}）
  const result: SqlResultSet = { columns: [], rows: [], end: null, error: null, hasColumns: false };
  let payload: unknown = resp.data;
  if (typeof payload === 'string') {
    try { payload = JSON.parse(payload); } catch { /* keep string */ }
  }
  if (payload && typeof payload === 'object') {
    const o = payload as Record<string, unknown>;
    result.error = {
      code: typeof o.code === 'string' ? o.code : `http_${resp.status}`,
      message: typeof o.message === 'string' ? o.message : `HTTP ${resp.status}`,
    };
  } else {
    result.error = { code: `http_${resp.status}`, message: `HTTP ${resp.status}` };
  }
  return result;
}

/**
 * 把 ndjson 行映射成对象数组（按 columns 名取值）。便于 n-data-table 直接绑定。
 */
export function rowsToObjects<T extends Record<string, unknown>>(rs: SqlResultSet): T[] {
  return rs.rows.map((row) => {
    const o: Record<string, unknown> = {};
    rs.columns.forEach((c, i) => { o[c] = row[i]; });
    return o as T;
  });
}

/** SQL 字符串字面量转义：单引号双写。 */
export function quote(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

/** SQL 标识符校验（与服务端 `IsValidName` 保持宽松一致：字母数字 + _，首字符必须字母）。 */
export function isValidIdentifier(name: string): boolean {
  return /^[A-Za-z][A-Za-z0-9_]*$/.test(name);
}
