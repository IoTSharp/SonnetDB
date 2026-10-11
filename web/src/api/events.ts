/**
 * SSE 客户端封装：基于浏览器原生 EventSource，订阅服务端 `/v1/events`。
 *
 * - token 通过 query string `?access_token=` 传递（EventSource 不支持自定义 header）。
 * - 失败时由浏览器内置的指数退避重连机制处理；本封装额外处理 401 — 直接关闭并通知调用方。
 * - channels 参数转换为 `?stream=metrics,slow_query,db`，缺省订阅全部。
 */
export type ServerEventType = 'metrics' | 'slow_query' | 'db' | 'hello';

export interface ServerEventPayload {
  type: ServerEventType;
  /** 服务端事件产生时间（Unix ms）。来自 SSE id 字段，hello 事件可能为 0。 */
  timestampMs: number;
  /** 已解析的 JSON 数据。 */
  data: unknown;
}

export interface MetricsSnapshot {
  uptimeSeconds: number;
  databases: number;
  sqlRequests: number;
  sqlErrors: number;
  rowsInserted: number;
  rowsReturned: number;
  subscriberCount: number;
  perDatabaseSegments: Record<string, number>;
}

export interface SlowQueryPayload {
  database: string;
  sql: string;
  elapsedMs: number;
  rowCount: number;
  recordsAffected: number;
  failed: boolean;
}

export interface DatabasePayload {
  database: string;
  /** "created" / "dropped" */
  action: string;
}

export interface SseSubscribeOptions {
  channels?: ServerEventType[];
  onEvent: (evt: ServerEventPayload) => void;
  onError?: (status: 'error' | 'unauthorized') => void;
  onOpen?: () => void;
}

/**
 * 订阅 SSE 流，返回一个关闭函数。
 */
export function subscribeServerEvents(token: string, opts: SseSubscribeOptions): () => void {
  const params = new URLSearchParams();
  params.set('access_token', token);
  if (opts.channels && opts.channels.length > 0) {
    params.set('stream', opts.channels.join(','));
  }
  const url = `${import.meta.env.BASE_URL}v1/events?${params.toString()}`;
  const es = new EventSource(url);

  const handlers: Array<[string, (e: MessageEvent) => void]> = [];
  function on(type: string): void {
    const h = (e: MessageEvent) => {
      let data: unknown = null;
      try { data = JSON.parse(e.data); } catch { data = e.data; }
      const id = Number(e.lastEventId);
      opts.onEvent({
        type: type as ServerEventType,
        timestampMs: Number.isFinite(id) ? id : 0,
        data,
      });
    };
    es.addEventListener(type, h as EventListener);
    handlers.push([type, h]);
  }
  on('hello');
  on('metrics');
  on('slow_query');
  on('db');

  es.onopen = () => opts.onOpen?.();
  es.onerror = () => {
    // EventSource 在 401/网络错误都会触发 onerror；readyState=CLOSED 表示无法恢复
    if (es.readyState === EventSource.CLOSED) {
      opts.onError?.('unauthorized');
    } else {
      opts.onError?.('error');
    }
  };

  return () => {
    for (const [type, h] of handlers) {
      es.removeEventListener(type, h as EventListener);
    }
    es.close();
  };
}
