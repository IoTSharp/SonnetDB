import { defineStore } from 'pinia';
import { computed, ref, watch } from 'vue';
import {
  subscribeServerEvents,
  type DatabasePayload,
  type MetricsSnapshot,
  type ServerEventPayload,
  type SlowQueryPayload,
} from '@/api/events';
import { useAuthStore } from '@/stores/auth';

const SLOW_QUERY_BUFFER = 100;
const DB_EVENT_BUFFER = 100;

export type SseStatus = 'idle' | 'connecting' | 'open' | 'error' | 'unauthorized';

export interface SlowQueryEntry extends SlowQueryPayload {
  receivedAt: number;
}

export interface DatabaseEventEntry extends DatabasePayload {
  receivedAt: number;
}

/**
 * 全局事件流 store：在用户登录后建立 SSE 长连接，分发到 metrics / slowQueries / dbEvents 三类响应式状态。
 * 视图层只需读取 store；订阅切换由本 store 的 connect/disconnect 内部管理。
 */
export const useEventsStore = defineStore('events', () => {
  const auth = useAuthStore();

  const status = ref<SseStatus>('idle');
  const lastError = ref<string>('');
  const metrics = ref<MetricsSnapshot | null>(null);
  const metricsUpdatedAt = ref<number>(0);
  const slowQueries = ref<SlowQueryEntry[]>([]);
  const dbEvents = ref<DatabaseEventEntry[]>([]);
  const dbEventBumper = ref(0); // 简易版本号：DB 事件来一次就 +1，便于其它 store/view 监听

  let unsubscribe: (() => void) | null = null;

  function handleEvent(evt: ServerEventPayload): void {
    switch (evt.type) {
      case 'hello':
        status.value = 'open';
        break;
      case 'metrics':
        metrics.value = evt.data as MetricsSnapshot;
        metricsUpdatedAt.value = evt.timestampMs || Date.now();
        break;
      case 'slow_query': {
        const payload = evt.data as SlowQueryPayload;
        slowQueries.value = [
          { ...payload, receivedAt: evt.timestampMs || Date.now() },
          ...slowQueries.value,
        ].slice(0, SLOW_QUERY_BUFFER);
        break;
      }
      case 'db': {
        const payload = evt.data as DatabasePayload;
        dbEvents.value = [
          { ...payload, receivedAt: evt.timestampMs || Date.now() },
          ...dbEvents.value,
        ].slice(0, DB_EVENT_BUFFER);
        dbEventBumper.value += 1;
        break;
      }
    }
  }

  function connect(): void {
    if (unsubscribe) return;
    const token = auth.state?.token;
    if (!token) return;
    status.value = 'connecting';
    lastError.value = '';
    unsubscribe = subscribeServerEvents(token, {
      onEvent: handleEvent,
      onOpen: () => { status.value = 'open'; },
      onError: (kind) => {
        status.value = kind;
        if (kind === 'unauthorized') {
          lastError.value = 'SSE 鉴权失败或连接已关闭。';
          // 让 EventSource 完全关闭
          unsubscribe?.();
          unsubscribe = null;
          auth.expire();
        } else {
          lastError.value = 'SSE 暂时断开，浏览器将自动重连。';
        }
      },
    });
  }

  function disconnect(): void {
    unsubscribe?.();
    unsubscribe = null;
    status.value = 'idle';
    metrics.value = null;
    metricsUpdatedAt.value = 0;
    slowQueries.value = [];
    dbEvents.value = [];
    dbEventBumper.value = 0;
  }

  // 登录态变化时自动建立 / 断开连接
  watch(
    () => auth.isAuthenticated,
    (now) => {
      if (now) connect();
      else disconnect();
    },
    { immediate: false },
  );

  const isLive = computed(() => status.value === 'open');

  return {
    status, isLive, lastError,
    metrics, metricsUpdatedAt,
    slowQueries, dbEvents, dbEventBumper,
    connect, disconnect,
  };
});
