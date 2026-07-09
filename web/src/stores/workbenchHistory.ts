import { defineStore } from 'pinia';
import { computed, ref, watch } from 'vue';

export type WorkbenchHistoryKind = 'query' | 'operation';
export type WorkbenchHistoryStatus = 'success' | 'error' | 'dry-run' | 'cancelled';

export interface WorkbenchHistoryEntry {
  id: string;
  kind: WorkbenchHistoryKind;
  status: WorkbenchHistoryStatus;
  title: string;
  target: string;
  database: string;
  connectionId: string;
  connectionName: string;
  model: string;
  action: string;
  command: string;
  summary: string;
  rowCount?: number;
  recordsAffected?: number;
  elapsedMs?: number;
  createdAt: number;
}

interface StoredWorkbenchHistory {
  entries: WorkbenchHistoryEntry[];
}

const StorageKey = 'sndb.workbench.history.v1';
const MaxEntries = 200;

function now(): number {
  return Date.now();
}

function makeId(prefix: string): string {
  return `${prefix}_${now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function normalizeEntry(input: Partial<WorkbenchHistoryEntry>): WorkbenchHistoryEntry {
  const ts = now();
  return {
    id: typeof input.id === 'string' && input.id ? input.id : makeId('hist'),
    kind: input.kind === 'operation' ? 'operation' : 'query',
    status: input.status === 'error'
      ? 'error'
      : input.status === 'dry-run'
        ? 'dry-run'
        : input.status === 'cancelled'
          ? 'cancelled'
          : 'success',
    title: typeof input.title === 'string' ? input.title : '',
    target: typeof input.target === 'string' ? input.target : '',
    database: typeof input.database === 'string' ? input.database : '',
    connectionId: typeof input.connectionId === 'string' ? input.connectionId : '',
    connectionName: typeof input.connectionName === 'string' ? input.connectionName : '',
    model: typeof input.model === 'string' ? input.model : '',
    action: typeof input.action === 'string' ? input.action : '',
    command: typeof input.command === 'string' ? input.command : '',
    summary: typeof input.summary === 'string' ? input.summary : '',
    rowCount: typeof input.rowCount === 'number' ? input.rowCount : undefined,
    recordsAffected: typeof input.recordsAffected === 'number' ? input.recordsAffected : undefined,
    elapsedMs: typeof input.elapsedMs === 'number' ? input.elapsedMs : undefined,
    createdAt: typeof input.createdAt === 'number' ? input.createdAt : ts,
  };
}

function loadState(): StoredWorkbenchHistory {
  try {
    const raw = localStorage.getItem(StorageKey);
    if (!raw) return { entries: [] };
    const parsed = JSON.parse(raw) as Partial<StoredWorkbenchHistory>;
    return {
      entries: Array.isArray(parsed.entries)
        ? parsed.entries.slice(0, MaxEntries).map((entry) => normalizeEntry(entry))
        : [],
    };
  } catch {
    return { entries: [] };
  }
}

function saveState(state: StoredWorkbenchHistory): void {
  try {
    localStorage.setItem(StorageKey, JSON.stringify(state));
  } catch {
    // 浏览器可能禁用本地存储，历史记录在内存态仍可使用。
  }
}

export const useWorkbenchHistoryStore = defineStore('workbenchHistory', () => {
  const initial = loadState();
  const entries = ref<WorkbenchHistoryEntry[]>(initial.entries);

  const recentEntries = computed(() =>
    [...entries.value].sort((a, b) => b.createdAt - a.createdAt));

  function record(input: Omit<WorkbenchHistoryEntry, 'id' | 'createdAt'> & Partial<Pick<WorkbenchHistoryEntry, 'id' | 'createdAt'>>): void {
    entries.value = [
      normalizeEntry(input),
      ...entries.value.filter((entry) => entry.id !== input.id),
    ].slice(0, MaxEntries);
  }

  function clear(): void {
    entries.value = [];
  }

  watch(
    entries,
    () => saveState({ entries: entries.value }),
    { deep: true },
  );

  return {
    entries,
    recentEntries,
    record,
    clear,
  };
});
