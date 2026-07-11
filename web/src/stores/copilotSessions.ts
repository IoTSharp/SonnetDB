import type { AxiosInstance } from 'axios';
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import {
  clearCopilotConversations,
  deleteCopilotConversation,
  listCopilotConversations,
  listCopilotMessages,
  upsertCopilotConversation,
  type CopilotConversationSummary,
  type CopilotMessage,
} from '@/api/copilot';
import { CONTROL_PLANE_KEY } from '@/stores/sqlConsole';

/**
 * 单条 Copilot 会话。服务端是唯一持久化来源，本 store 只保留当前页面的内存镜像。
 */
export interface CopilotSession {
  id: string;
  title: string;
  db: string;
  messages: CopilotMessage[];
  messageCount: number;
  createdAt: number;
  updatedAt: number;
  messagesLoaded: boolean;
}

const TITLE_MAX_LEN = 64;

function genId(): string {
  return `sndb_${crypto.randomUUID().replaceAll('-', '')}`;
}

function toSession(summary: CopilotConversationSummary, existing?: CopilotSession): CopilotSession {
  return {
    id: summary.id,
    title: summary.title,
    db: summary.database ?? '',
    messages: existing?.messages ?? [],
    messageCount: summary.messageCount,
    createdAt: Date.parse(summary.createdAtUtc),
    updatedAt: Date.parse(summary.updatedAtUtc),
    messagesLoaded: existing?.messagesLoaded ?? false,
  };
}

export const useCopilotSessionsStore = defineStore('copilotSessions', () => {
  const sessions = ref<CopilotSession[]>([]);
  const currentId = ref<string | null>(null);
  const loading = ref(false);
  const loaded = ref(false);

  const current = computed<CopilotSession | null>(() =>
    sessions.value.find((session) => session.id === currentId.value) ?? null,
  );

  const recent = computed<CopilotSession[]>(() =>
    [...sessions.value].sort((left, right) => right.updatedAt - left.updatedAt),
  );

  async function refresh(api: AxiosInstance): Promise<void> {
    loading.value = true;
    try {
      const summaries = await listCopilotConversations(api);
      const existing = new Map(sessions.value.map((session) => [session.id, session]));
      sessions.value = summaries.map((summary) => toSession(summary, existing.get(summary.id)));
      if (currentId.value && !sessions.value.some((session) => session.id === currentId.value)) {
        currentId.value = null;
      }
      loaded.value = true;
    } finally {
      loading.value = false;
    }
  }

  async function create(api: AxiosInstance, db: string): Promise<CopilotSession> {
    const summary = await upsertCopilotConversation(api, { id: genId(), title: '新会话', database: db || undefined });
    const session = toSession(summary);
    session.messagesLoaded = true;
    sessions.value.unshift(session);
    currentId.value = session.id;
    return session;
  }

  async function switchTo(api: AxiosInstance, id: string): Promise<void> {
    const session = sessions.value.find((item) => item.id === id);
    if (!session) return;
    currentId.value = id;
    if (!session.messagesLoaded) {
      session.messages = await listCopilotMessages(api, id);
      session.messageCount = session.messages.length;
      session.messagesLoaded = true;
    }
  }

  async function rename(api: AxiosInstance, id: string, title: string): Promise<void> {
    const session = sessions.value.find((item) => item.id === id);
    if (!session) return;
    const trimmed = title.trim();
    if (!trimmed) return;
    const summary = await upsertCopilotConversation(api, {
      id,
      title: trimmed.slice(0, TITLE_MAX_LEN),
      database: session.db || undefined,
    });
    Object.assign(session, toSession(summary, session));
  }

  async function remove(api: AxiosInstance, id: string): Promise<void> {
    await deleteCopilotConversation(api, id);
    const index = sessions.value.findIndex((session) => session.id === id);
    if (index >= 0) sessions.value.splice(index, 1);
    if (currentId.value === id) currentId.value = sessions.value[0]?.id ?? null;
  }

  async function clearAll(api: AxiosInstance): Promise<void> {
    await clearCopilotConversations(api);
    sessions.value = [];
    currentId.value = null;
  }

  function hideControlPlaneForRegularUser(): void {
    for (const session of sessions.value) {
      if (session.db === CONTROL_PLANE_KEY) session.db = '';
    }
  }

  function appendMessage(id: string, db: string, message: CopilotMessage): CopilotSession {
    const session = sessions.value.find((item) => item.id === id);
    if (!session) throw new Error('Copilot 会话尚未创建。');
    session.messages.push(message);
    session.messageCount = session.messages.length;
    session.updatedAt = Date.now();
    session.messagesLoaded = true;
    if (session.title === '新会话' && message.role === 'user' && message.content.trim()) {
      session.title = message.content.trim().slice(0, TITLE_MAX_LEN);
    }
    if (db && !session.db) session.db = db;
    return session;
  }

  return {
    sessions,
    currentId,
    current,
    recent,
    loading,
    loaded,
    refresh,
    create,
    switchTo,
    rename,
    remove,
    clearAll,
    hideControlPlaneForRegularUser,
    appendMessage,
  };
});
