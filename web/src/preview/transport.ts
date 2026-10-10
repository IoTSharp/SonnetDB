import { AxiosError, AxiosHeaders, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { previewLimits, previewRequestAction, previewSupported } from './policy';
import { clearPreviewSession, previewSession, registerPreviewRequest, retainPreviewBytes } from './session';
import { parseNdjson } from '../api/sql';

/** Conservative payload estimate: UTF-16 text plus per-token container/scalar overhead. */
export function estimatedPreviewBytes(text: string): number {
  let bytes = text.length * 2;
  for (const character of text) if (',:{}[]\n'.includes(character)) bytes += 64;
  return bytes;
}

/** Read incrementally, rejecting oversized chunks before concatenation or JSON parsing. */
export async function readPreviewBody(response: Response, signal: AbortSignal, maximum = previewLimits.bytes): Promise<Uint8Array> {
  const declared = Number(response.headers.get('content-length') ?? 0);
  if (declared > maximum) { await response.body?.cancel(); throw new Error('响应超过 Preview 4 MiB 上限。'); }
  const reader = response.body?.getReader();
  if (!reader) return new Uint8Array();
  const chunks: Uint8Array[] = [];
  let length = 0;
  const abort = () => { void reader.cancel(); };
  signal.addEventListener('abort', abort, { once: true });
  try {
    // Bound both chunk count and wall time (the caller owns the 30 second signal).
    for (let count = 0; count < 65536; count++) {
      signal.throwIfAborted();
      const item = await reader.read();
      signal.throwIfAborted();
      if (item.done) {
        const result = new Uint8Array(length);
        let offset = 0;
        for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.length; }
        return result;
      }
      if (length + item.value.length > maximum) throw new Error('响应超过 Preview 4 MiB 上限。');
      chunks.push(item.value); length += item.value.length;
    }
    throw new Error('响应分块数量超过 Preview 上限。');
  } finally { signal.removeEventListener('abort', abort); await reader.cancel().catch(() => undefined); reader.releaseLock(); }
}

let queue: Promise<void> = Promise.resolve();
let pending = 0;

export const previewAdapter: AxiosAdapter = async (config) => {
  if (!previewSupported) throw new Error('未知 Workbench profile；请求已拒绝。');
  if (pending >= 16) throw new Error('Preview 请求队列已满。');
  const generation = previewSession.value.generation;
  const controller = new AbortController();
  const unregister = registerPreviewRequest(controller);
  const timer = setTimeout(() => controller.abort(new Error('操作超过 30 秒。')), previewLimits.milliseconds);
  const cancelled = () => controller.abort();
  config.signal?.addEventListener?.('abort', cancelled, { once: true });
  if (config.signal?.aborted) controller.abort();
  const previous = queue;
  let release!: () => void;
  queue = new Promise<void>((resolve) => { release = resolve; });
  pending++;
  try {
    await previous;
    controller.signal.throwIfAborted();
    if (generation !== previewSession.value.generation) throw new Error('身份或数据库已变化。');
    return await send(config, controller.signal, generation);
  } finally {
    pending--; release(); clearTimeout(timer); unregister();
    config.signal?.removeEventListener?.('abort', cancelled);
  }
};

async function send(config: InternalAxiosRequestConfig, signal: AbortSignal, generation: number) {
  const url = new URL(config.url ?? '', new URL(config.baseURL ?? '/', window.location.origin));
  if (url.origin !== window.location.origin || url.username || url.password) throw new Error('Preview 仅允许同源连接。');
  if (config.params) for (const [key, value] of Object.entries(config.params)) if (value !== undefined && value !== null) url.searchParams.set(key, String(value));
  const method = (config.method ?? 'GET').toUpperCase();
  const headers = new Headers(config.headers.toJSON() as Record<string, string>);
  const data = typeof config.data === 'string' ? config.data : config.data == null ? '' : JSON.stringify(config.data);
  if (new TextEncoder().encode(data).length > previewLimits.inputBytes) throw new Error('输入超过 Preview 64 KiB 上限。');
  const body = data ? JSON.parse(data) as Record<string, unknown> : {};
  const action = previewRequestAction(method, url, body, headers);
  if (!action) throw new Error('本预览未开放此操作。');
  const database = /^\/v1\/db\/([^/]+)\//u.exec(url.pathname)?.[1];
  const isAccess = url.pathname.endsWith('/access');
  if (database && !isAccess && decodeURIComponent(database) !== previewSession.value.database) throw new Error('数据库上下文已失效，请重新选择。');
  if (database && !isAccess) {
    let accessResponse: Response;
    let access: { canRead?: boolean; canWrite?: boolean };
    try {
      accessResponse = await fetch(`${url.origin}/v1/db/${database}/access`, { headers: { Authorization: headers.get('Authorization') ?? '' }, signal, redirect: 'error' });
      access = JSON.parse(new TextDecoder().decode(await readPreviewBody(accessResponse, signal, 4096)));
    } catch (error) {
      if (generation === previewSession.value.generation) clearPreviewSession('无法确认权限；结果、草稿与审批已清除，请显式重读。', true);
      throw error;
    }
    if (generation !== previewSession.value.generation) throw new Error('过期权限响应已丢弃。');
    if (!accessResponse.ok || access.canRead !== true || typeof access.canWrite !== 'boolean' || (previewSession.value.canWrite && !access.canWrite)) {
      clearPreviewSession('权限已变化；结果、草稿和审批已清除。请重新选择数据库。', true);
      throw new AxiosError('权限已变化。', 'ERR_BAD_REQUEST', config, undefined, { data: {}, status: 403, statusText: 'Forbidden', headers: {}, config });
    }
    if (action === 'relation.insert.one' && !access.canWrite) throw new Error('当前账户只有读取权限。');
  }
  for (const key of ['limit', 'topK', 'previewMaxRows', 'maxCount']) {
    const maximum = key === 'topK' ? 100 : 1000;
    if (body[key] !== undefined && (!Number.isSafeInteger(body[key]) || Number(body[key]) < 1 || Number(body[key]) > maximum)) throw new Error('请求行数超过 Preview 上限。');
  }
  if (action === 'sql.read') body.previewMaxRows = Math.min(Number(body.previewMaxRows ?? 1000), 1000);
  for (const key of ['limit', 'max-keys']) {
    const value = url.searchParams.get(key);
    if (value !== null && (!Number.isSafeInteger(Number(value)) || Number(value) < 1 || Number(value) > 1000)) throw new Error('请求窗口超过 Preview 上限。');
  }
  headers.set('X-SonnetDB-Workbench-Profile', 'preview-1');
  headers.set('X-SonnetDB-Workbench-Action', action);
  let response: Response;
  let bytes: Uint8Array;
  try {
    response = await fetch(url, { method, headers, body: method === 'GET' || method === 'HEAD' ? undefined : JSON.stringify(body), signal, redirect: 'error' });
    bytes = await readPreviewBody(response, signal);
  } catch (error) {
    if (generation === previewSession.value.generation) clearPreviewSession(action === 'relation.insert.one'
      ? '写入状态未知。取消不等于回滚；请核对服务器，旧审批不会重放。'
      : signal.aborted ? '读取已取消；不证明服务器扫描已停止。请显式重读。' : '连接中断或响应超限；不完整结果已清除。请显式重读。', true);
    throw error;
  }
  if (generation !== previewSession.value.generation) throw new Error('过期响应已丢弃。');
  if (action === 'relation.insert.one' && (response.status === 408 || response.status >= 500)) {
    clearPreviewSession('写入状态未知；服务器未返回可确认的终态，请核对数据，旧审批不会重放。', true);
    throw new Error('写入状态未知。');
  }
  if ([401, 403].includes(response.status)) clearPreviewSession('凭据失效或权限不足；旧载荷已清除。请重新登录或选择数据库。', true);
  let result: unknown;
  if (config.responseType === 'blob') result = new Blob([bytes], { type: response.headers.get('content-type') ?? '' });
  else if (config.responseType === 'arraybuffer') result = bytes.buffer;
  else {
    const text = new TextDecoder().decode(bytes);
    if (estimatedPreviewBytes(text) > previewLimits.bytes) { clearPreviewSession(action === 'relation.insert.one' ? '写入状态未知；响应解码后超限，请核对数据，旧审批不会重放。' : '解码后载荷超过 4 MiB，结果已清除。', true); throw new Error('解码后载荷超限。'); }
    if (response.ok && url.pathname === '/v1/db') {
      const list = JSON.parse(text) as { databases?: unknown };
      if (!Array.isArray(list.databases) || list.databases.length > 1000 || !list.databases.every((name) => typeof name === 'string')) {
        clearPreviewSession('数据库列表无效或超过1000项，请缩小实例范围。', true);
        throw new Error('Preview 数据库列表校验失败。');
      }
    }
    if (response.ok && (action === 'sql.read' || action === 'relation.insert.one')) {
      const parsed = parseNdjson(text);
      const maximum = Number(body.previewMaxRows ?? 1000);
      if (parsed.rows.length > maximum || parsed.error?.code === 'invalid_sql_response' || parsed.error?.code === 'incomplete_sql_response') {
        clearPreviewSession(action === 'relation.insert.one' ? '写入状态未知；响应不完整，旧审批不会重放。' : '响应不完整或行数超限；结果已清除，请显式重读。', true);
        throw new Error('Preview SQL 响应校验失败。');
      }
    }
    result = text;
  }
  if (database && !isAccess) {
    try { retainPreviewBytes(typeof result === 'string' ? estimatedPreviewBytes(result) : bytes.length, previewLimits.bytes); }
    catch (error) { clearPreviewSession('累计结果超过 4 MiB；请显式重读。', true); throw error; }
  }
  const value = { data: result, status: response.status, statusText: response.statusText, headers: AxiosHeaders.from(Object.fromEntries(response.headers)), config };
  if (config.validateStatus && !config.validateStatus(value.status)) throw new AxiosError(`HTTP ${value.status}`, 'ERR_BAD_RESPONSE', config, undefined, value);
  return value;
}
