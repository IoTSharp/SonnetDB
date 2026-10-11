import { defineStore } from 'pinia';
import { computed, ref, watch } from 'vue';

export type WorkbenchHistoryKind = 'query' | 'operation';
export type WorkbenchHistoryStatus = 'success' | 'error' | 'dry-run' | 'cancelled' | 'unknown';
export type WorkbenchHistoryCompleteness = 'complete' | 'truncated' | 'partial' | 'unknown';

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
  /** 结果集合的完整性；unknown 表示未取得服务端终态。 */
  completeness?: WorkbenchHistoryCompleteness;
  /** 敏感命令不进入本地历史载荷，也不能从历史恢复。 */
  sensitive?: boolean;
  createdAt: number;
}

interface StoredWorkbenchHistory {
  entries: WorkbenchHistoryEntry[];
}

const StorageKey = 'sndb.workbench.history.v1';
const MaxEntries = 200;
const MaxTextLength = 16_384;

function now(): number {
  return Date.now();
}

function makeId(prefix: string): string {
  return `${prefix}_${now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

const JsonSensitiveKeyPattern = /(?<!\\)"(?:authorization|access[_ -]?token|refresh[_ -]?token|id[_ -]?token|password|passwd|secret|client[_ -]?secret|token|api[_ -]?key|apikey|private[_ -]?key|credentials?)"\s*:\s*/giu;

function findJsonValueEnd(value: string, start: number): number {
  if (start >= value.length) return start;
  const first = value[start];
  if (first === '"') {
    let escaped = false;
    for (let index = start + 1; index < value.length; index += 1) {
      const character = value[index];
      if (escaped) {
        escaped = false;
      } else if (character === '\\') {
        escaped = true;
      } else if (character === '"') {
        return index + 1;
      }
    }
    return value.length;
  }

  if (first === '{' || first === '[') {
    const stack: string[] = [first === '{' ? '}' : ']'];
    let escaped = false;
    let inString = false;
    for (let index = start + 1; index < value.length; index += 1) {
      const character = value[index];
      if (inString) {
        if (escaped) {
          escaped = false;
        } else if (character === '\\') {
          escaped = true;
        } else if (character === '"') {
          inString = false;
        }
        continue;
      }
      if (character === '"') {
        inString = true;
      } else if (character === '{' || character === '[') {
        stack.push(character === '{' ? '}' : ']');
      } else if (character === '}' || character === ']') {
        if (stack.at(-1) !== character) return start;
        stack.pop();
        if (stack.length === 0) return index + 1;
      }
    }
    return value.length;
  }

  let index = start;
  while (index < value.length && !/[\s,}\]]/u.test(value[index])) index += 1;
  return index;
}

function redactJsonSensitiveFields(value: string): string {
  let redacted = '';
  let cursor = 0;
  JsonSensitiveKeyPattern.lastIndex = 0;
  while (JsonSensitiveKeyPattern.exec(value) !== null) {
    const valueStart = JsonSensitiveKeyPattern.lastIndex;
    const valueEnd = findJsonValueEnd(value, valueStart);
    if (valueEnd <= valueStart) {
      JsonSensitiveKeyPattern.lastIndex = valueStart + 1;
      continue;
    }
    redacted += value.slice(cursor, valueStart);
    redacted += '"[redacted]"';
    cursor = valueEnd;
    JsonSensitiveKeyPattern.lastIndex = valueEnd;
  }
  return redacted + value.slice(cursor);
}

/** 对历史中的命令和摘要做有界脱敏，避免凭据进入 localStorage 或恢复输入。 */
export function redactWorkbenchText(value: string, maxLength = MaxTextLength): string {
  const redacted = redactJsonSensitiveFields(value)
    .replace(/(?<!["\p{L}\p{N}_])(authorization\s+(?:bearer\s+)?)[^\s,;"]+/giu, '$1[redacted]')
    .replace(/(?<!["\p{L}\p{N}_])((?:password|passwd|secret|token|access[_ -]?token|api[_ -]?key)\s*(?:[:=]|\s)\s*)(['"]?)[^\s,'";]+\2/giu, '$1[redacted]')
    .replace(/(?<!["\p{L}\p{N}_])(bearer\s+)[^\s,;"]+/giu, '$1[redacted]');
  return redacted.length > maxLength ? `${redacted.slice(0, maxLength)}…` : redacted;
}

export function normalizeWorkbenchHistoryStatus(value: unknown): WorkbenchHistoryStatus {
  if (value === 'success' || value === 'error' || value === 'dry-run' || value === 'cancelled' || value === 'unknown') {
    return value;
  }
  return 'unknown';
}

function normalizeEntry(input: Partial<WorkbenchHistoryEntry>): WorkbenchHistoryEntry {
  const ts = now();
  const sensitive = input.sensitive === true;
  const status = normalizeWorkbenchHistoryStatus(
    input.completeness === 'unknown' && input.status === 'success' ? 'unknown' : input.status,
  );
  const command = sensitive ? '' : redactWorkbenchText(typeof input.command === 'string' ? input.command : '');
  const summary = redactWorkbenchText(typeof input.summary === 'string' ? input.summary : '');
  return {
    id: typeof input.id === 'string' && input.id ? input.id : makeId('hist'),
    kind: input.kind === 'operation' ? 'operation' : 'query',
    status,
    title: typeof input.title === 'string' ? input.title : '',
    target: typeof input.target === 'string' ? input.target : '',
    database: typeof input.database === 'string' ? input.database : '',
    connectionId: typeof input.connectionId === 'string' ? input.connectionId : '',
    connectionName: typeof input.connectionName === 'string' ? input.connectionName : '',
    model: typeof input.model === 'string' ? input.model : '',
    action: typeof input.action === 'string' ? input.action : '',
    command,
    summary: status === 'unknown' && !summary
      ? '执行结果待核对；未收到服务端终态，不会自动重放。'
      : summary,
    rowCount: typeof input.rowCount === 'number' ? input.rowCount : undefined,
    recordsAffected: typeof input.recordsAffected === 'number' ? input.recordsAffected : undefined,
    elapsedMs: typeof input.elapsedMs === 'number' ? input.elapsedMs : undefined,
    completeness: input.completeness === 'complete'
      || input.completeness === 'truncated'
      || input.completeness === 'partial'
      || input.completeness === 'unknown'
      ? input.completeness
      : status === 'unknown' ? 'unknown' : undefined,
    sensitive,
    createdAt: typeof input.createdAt === 'number' ? input.createdAt : ts,
  };
}

function loadState(): StoredWorkbenchHistory {
  if (import.meta.env?.VITE_WORKBENCH_PROFILE && import.meta.env.VITE_WORKBENCH_PROFILE !== 'full') return { entries: [] };
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
  if (import.meta.env?.VITE_WORKBENCH_PROFILE && import.meta.env.VITE_WORKBENCH_PROFILE !== 'full') return;
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
    if (import.meta.env?.VITE_WORKBENCH_PROFILE && import.meta.env.VITE_WORKBENCH_PROFILE !== 'full') return;
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
