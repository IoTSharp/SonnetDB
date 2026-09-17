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

export enum ScalarKind {
  Null = 0,
  String = 1,
  Integer = 2,
  Double = 3,
  Boolean = 4,
}

export interface SqlParameterValue {
  kind: ScalarKind;
  stringValue?: string | null;
  integerValue?: number | null;
  doubleValue?: number | null;
  booleanValue?: boolean | null;
}

export type SqlParameters = Record<string, SqlParameterValue>;

export interface SqlStatementRequest {
  sql: string;
  parameters?: SqlParameters;
}

/**
 * 把后端 ndjson 响应解析成结构化 SqlResultSet。
 * - 第一行为 meta（{type:"meta",columns:[...]}）。
 * - 中间每行为 JSON 数组，按 meta.columns 顺序对应。
 * - 末行为 end 或 error。
 */
export function parseNdjson(body: string): SqlResultSet {
  return singleResult(parseNdjsonResults(body));
}

/**
 * 把批量 SQL ndjson 流拆成多个结果集。每个 meta/end 或 end/error 边界对应一条语句。
 */
export function parseNdjsonResults(body: string): SqlResultSet[] {
  const results: SqlResultSet[] = [];
  let result: SqlResultSet = emptyResultSet();
  const lines = body.split(/\r?\n/).filter((l) => l.length > 0);
  for (const line of lines) {
    let obj: unknown;
    try {
      obj = JSON.parse(line);
    } catch {
      result.error = { code: 'invalid_sql_response', message: 'SQL 响应包含损坏的 JSON，结果可能不完整。' };
      results.push(result);
      return results;
    }
    if (Array.isArray(obj)) {
      if (!result.hasColumns || obj.length !== result.columns.length) {
        result.error = { code: 'invalid_sql_response', message: 'SQL 响应行与列定义不匹配。' };
        results.push(result);
        return results;
      }
      result.rows.push(obj);
      continue;
    }
    if (obj && typeof obj === 'object') {
      const o = obj as Record<string, unknown>;
      if (o.type === 'meta' && Array.isArray(o.columns) && o.columns.every((column) => typeof column === 'string')) {
        if (hasResultContent(result)) {
          result.error = { code: 'incomplete_sql_response', message: 'SQL 结果缺少完成标记，不能确认执行成功。' };
          results.push(result);
          return results;
        }
        result.columns = o.columns as string[];
        result.hasColumns = true;
        continue;
      } else if (o.type === 'end') {
        const elapsedMs = o.elapsedMs ?? o.elapsedMilliseconds;
        if (!Number.isSafeInteger(o.rowCount) || Number(o.rowCount) < 0
          || !Number.isSafeInteger(o.recordsAffected) || Number(o.recordsAffected) < -1
          || typeof elapsedMs !== 'number' || !Number.isFinite(elapsedMs) || elapsedMs < 0
          || o.rowCount !== result.rows.length) {
          result.error = { code: 'invalid_sql_response', message: 'SQL 完成标记无效或返回行数不完整。' };
          results.push(result);
          return results;
        }
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
        results.push(result);
        result = emptyResultSet();
        continue;
      } else if (typeof o.message === 'string' && (o.code || o.error || o.type === 'error')) {
        result.error = {
          type: 'error',
          code: typeof o.code === 'string' ? o.code : typeof o.error === 'string' ? o.error : undefined,
          message: o.message,
        };
        results.push(result);
        result = emptyResultSet();
        continue;
      }
    }
    result.error = { code: 'invalid_sql_response', message: 'SQL 响应包含无法识别的记录，结果可能不完整。' };
    results.push(result);
    return results;
  }
  if (hasResultContent(result)) {
    result.error = { code: 'incomplete_sql_response', message: 'SQL 响应提前结束，结果可能不完整。' };
    results.push(result);
  }
  if (results.length === 0) {
    result.error = { code: 'incomplete_sql_response', message: 'SQL 响应为空，不能确认执行成功。' };
    results.push(result);
  }
  return results;
}

/**
 * 执行控制面 SQL（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS / SHOW DATABASES 等）。
 * 走服务端 <c>POST /v1/sql</c> 端点（admin only）。
 */
export async function execControlPlaneSql(
  api: AxiosInstance,
  sql: string,
  parameters?: SqlParameters,
  signal?: AbortSignal,
): Promise<SqlResultSet> {
  return doExec(api, '/v1/sql', { sql, parameters }, signal);
}

/**
 * 执行数据面 SQL（INSERT / SELECT / DELETE / CREATE MEASUREMENT 等）。
 */
export async function execDataSql(
  api: AxiosInstance,
  db: string,
  sql: string,
  parameters?: SqlParameters,
  signal?: AbortSignal,
): Promise<SqlResultSet> {
  return doExec(api, `/v1/db/${encodeURIComponent(db)}/sql`, { sql, parameters }, signal);
}

/**
 * 批量执行数据面 SQL。服务端按顺序执行，事务语句可放在同一批中。
 */
export async function execDataSqlBatch(
  api: AxiosInstance,
  db: string,
  statements: SqlStatementRequest[],
  signal?: AbortSignal,
): Promise<SqlResultSet[]> {
  const results = await doExecMany(api, `/v1/db/${encodeURIComponent(db)}/sql/batch`, { statements }, signal);
  if (statements.length > 0 && results.length > statements.length) {
    const failed = results.find((result) => result.error) ?? results[statements.length];
    results[statements.length - 1].error = failed.error ?? {
      code: 'invalid_sql_response', message: 'SQL 批次响应结果数量超出请求语句数。',
    };
    return results.slice(0, statements.length);
  }
  return results;
}

async function doExec(api: AxiosInstance, url: string, payload: SqlStatementRequest, signal?: AbortSignal): Promise<SqlResultSet> {
  const results = await doExecMany(api, url, normalizeSqlStatementPayload(payload), signal);
  return singleResult(results);
}

async function doExecMany(api: AxiosInstance, url: string, requestBody: unknown, signal?: AbortSignal): Promise<SqlResultSet[]> {
  const resp = await api.post(url, requestBody, {
    responseType: 'text',
    transformResponse: (v) => v,
    validateStatus: () => true,
    signal,
  });
  const ct = resp.headers['content-type']?.toString() ?? '';
  if (typeof resp.data === 'string' && ct.includes('ndjson')) {
    return parseNdjsonResults(resp.data);
  }
  // 非 ndjson → JSON 错误体（{code, message}）
  const result: SqlResultSet = emptyResultSet();
  let errorPayload: unknown = resp.data;
  if (typeof errorPayload === 'string') {
    try { errorPayload = JSON.parse(errorPayload); } catch { /* keep string */ }
  }
  if (errorPayload && typeof errorPayload === 'object') {
    const o = errorPayload as Record<string, unknown>;
    result.error = {
      code: typeof o.code === 'string' ? o.code : typeof o.error === 'string' ? o.error : `http_${resp.status}`,
      message: typeof o.message === 'string' ? o.message : `HTTP ${resp.status}`,
    };
  } else {
    result.error = { code: `http_${resp.status}`, message: `HTTP ${resp.status}` };
  }
  return [result];
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

export function sqlParameterFromValue(value: unknown): SqlParameterValue {
  if (value === null || value === undefined) {
    return { kind: ScalarKind.Null };
  }
  if (typeof value === 'boolean') {
    return { kind: ScalarKind.Boolean, booleanValue: value };
  }
  if (typeof value === 'number') {
    return Number.isInteger(value)
      ? { kind: ScalarKind.Integer, integerValue: value }
      : { kind: ScalarKind.Double, doubleValue: value };
  }
  return { kind: ScalarKind.String, stringValue: String(value) };
}

function emptyResultSet(): SqlResultSet {
  return { columns: [], rows: [], end: null, error: null, hasColumns: false };
}

function singleResult(results: SqlResultSet[]): SqlResultSet {
  const result = results[0] ?? emptyResultSet();
  if (results.length !== 1) {
    result.error = results.find((item) => item.error)?.error ?? {
      code: 'invalid_sql_response', message: '单条 SQL 请求返回了非预期的多个结果。',
    };
  }
  return result;
}

function hasResultContent(result: SqlResultSet): boolean {
  return result.hasColumns || result.rows.length > 0 || result.end !== null || result.error !== null;
}

function normalizeSqlStatementPayload(payload: SqlStatementRequest): SqlStatementRequest {
  if (payload.parameters && Object.keys(payload.parameters).length === 0) {
    return { sql: payload.sql };
  }
  return payload;
}
