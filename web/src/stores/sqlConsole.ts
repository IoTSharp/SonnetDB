import { defineStore } from 'pinia';
import { computed, ref, watch } from 'vue';
import type { SqlResultSet } from '@/api/sql';

export const CONTROL_PLANE_KEY = '__control_plane__';

const STORAGE_KEY = 'sndb.sql.console.tabs.v1';
const MaxTabs = 30;
const DefaultStarterSql = ['SHOW MEASUREMENTS', 'SHOW DATABASES'];

export interface PendingSqlExecution {
  db: string;
  sql: string;
  runImmediately: boolean;
  tabId?: string;
  title?: string;
}

export interface SqlConsoleExecutedStatement {
  id: string;
  sql: string;
  result: SqlResultSet;
  createdAt: number;
  source?: 'manual' | 'copilot' | 'meta';
}

export interface SqlConsoleTab {
  id: string;
  title: string;
  db: string;
  sql: string;
  results: SqlConsoleExecutedStatement[];
  summary: string;
  errorMsg: string;
  ranOnce: boolean;
  source: 'manual' | 'copilot';
  createdAt: number;
  updatedAt: number;
}

interface StoredState {
  tabs: SqlConsoleTab[];
  activeTabId: string | null;
}

function now(): number {
  return Date.now();
}

function makeId(prefix: string): string {
  return `${prefix}_${now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function defaultTab(): SqlConsoleTab {
  const ts = now();
  return {
    id: makeId('sqltab'),
    title: 'SQL 1',
    db: '',
    sql: 'SHOW DATABASES',
    results: [],
    summary: '',
    errorMsg: '',
    ranOnce: false,
    source: 'manual',
    createdAt: ts,
    updatedAt: ts,
  };
}

function defaultDataPlaneSql(db: string): string {
  return db ? 'SHOW MEASUREMENTS' : '';
}

function normalizeStarterSql(sql: string): string {
  const compact = sql.replace(/[\s;]+/g, '').toUpperCase();
  if (!compact) return sql;

  for (const starter of DefaultStarterSql) {
    const token = starter.replace(/\s+/g, '').toUpperCase();
    const repeatCount = compact.length / token.length;
    if (repeatCount >= 2 && Number.isInteger(repeatCount) && compact === token.repeat(repeatCount)) {
      return starter;
    }
  }

  return sql;
}

function deriveTitle(sql: string, fallback: string): string {
  const text = sql.replace(/\s+/g, ' ').trim();
  if (!text) return fallback;
  return text.length > 28 ? `${text.slice(0, 25)}...` : text;
}

function normalizeResultEnd(end: unknown): SqlResultSet['end'] {
  if (!end || typeof end !== 'object') return null;
  const value = end as Record<string, unknown>;
  return {
    type: 'end',
    rowCount: typeof value.rowCount === 'number' ? value.rowCount : 0,
    recordsAffected: typeof value.recordsAffected === 'number' ? value.recordsAffected : -1,
    elapsedMs: typeof value.elapsedMs === 'number'
      ? value.elapsedMs
      : typeof value.elapsedMilliseconds === 'number'
        ? value.elapsedMilliseconds
        : 0,
  };
}

function normalizeResultSet(result: Partial<SqlResultSet> | null | undefined): SqlResultSet {
  const columns = Array.isArray(result?.columns)
    ? result.columns.filter((column): column is string => typeof column === 'string')
    : [];
  const rows = Array.isArray(result?.rows)
    ? result.rows.filter(Array.isArray) as unknown[][]
    : [];
  return {
    columns,
    rows,
    end: normalizeResultEnd(result?.end),
    error: typeof result?.error?.message === 'string'
      ? {
          code: typeof result.error?.code === 'string' ? result.error.code : undefined,
          message: result.error.message,
        }
      : null,
    hasColumns: typeof result?.hasColumns === 'boolean' ? result.hasColumns : columns.length > 0,
  };
}

function normalizeExecutedStatement(input: Partial<SqlConsoleExecutedStatement>): SqlConsoleExecutedStatement {
  const ts = now();
  return {
    id: typeof input.id === 'string' && input.id ? input.id : makeId('stmt'),
    sql: typeof input.sql === 'string' ? input.sql : '',
    result: normalizeResultSet(input.result),
    createdAt: typeof input.createdAt === 'number' ? input.createdAt : ts,
    source: input.source === 'copilot' ? 'copilot' : input.source === 'meta' ? 'meta' : 'manual',
  };
}

function normalizeTab(input: Partial<SqlConsoleTab>, index: number): SqlConsoleTab {
  const fallback = defaultTab();
  const ts = now();
  const normalizedTitle = typeof input.title === 'string' && input.title
    ? input.title.replace(/^查询\s+/u, 'SQL ')
    : `SQL ${index + 1}`;
  return {
    ...fallback,
    ...input,
    id: typeof input.id === 'string' && input.id ? input.id : makeId('sqltab'),
    title: normalizedTitle,
    db: typeof input.db === 'string' ? input.db : '',
    sql: typeof input.sql === 'string' ? normalizeStarterSql(input.sql) : '',
    results: Array.isArray(input.results)
      ? input.results.map((item) => normalizeExecutedStatement(item))
      : [],
    summary: typeof input.summary === 'string' ? input.summary : '',
    errorMsg: typeof input.errorMsg === 'string' ? input.errorMsg : '',
    ranOnce: Boolean(input.ranOnce),
    source: input.source === 'copilot' ? 'copilot' : 'manual',
    createdAt: typeof input.createdAt === 'number' ? input.createdAt : ts,
    updatedAt: typeof input.updatedAt === 'number' ? input.updatedAt : ts,
  };
}

function loadState(): StoredState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      const tab = defaultTab();
      return { tabs: [tab], activeTabId: tab.id };
    }

    const parsed = JSON.parse(raw) as Partial<StoredState>;
    const tabs = Array.isArray(parsed.tabs)
      ? parsed.tabs.slice(0, MaxTabs).map((tab, index) => normalizeTab(tab, index))
      : [];
    if (tabs.length === 0) {
      const tab = defaultTab();
      return { tabs: [tab], activeTabId: tab.id };
    }

    const activeTabId = typeof parsed.activeTabId === 'string'
        && tabs.some((tab) => tab.id === parsed.activeTabId)
      ? parsed.activeTabId
      : tabs[0].id;
    return { tabs, activeTabId };
  } catch {
    const tab = defaultTab();
    return { tabs: [tab], activeTabId: tab.id };
  }
}

function saveState(state: StoredState): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    // localStorage 可能被浏览器策略禁用，忽略即可，内存态仍可用。
  }
}

export const useSqlConsoleStore = defineStore('sqlConsole', () => {
  const initial = loadState();
  const tabs = ref<SqlConsoleTab[]>(initial.tabs);
  const activeTabId = ref<string | null>(initial.activeTabId);
  const pendingExecution = ref<PendingSqlExecution | null>(null);

  const activeTab = computed<SqlConsoleTab | null>(() =>
    tabs.value.find((tab) => tab.id === activeTabId.value) ?? tabs.value[0] ?? null);

  /** SQL Console 当前正在编辑的 SQL 文本（供 CopilotDock 作为页面上下文）。 */
  const currentSql = computed(() => activeTab.value?.sql ?? '');

  /** SQL Console 当前选中的用户数据库；控制面返回空字符串，避免 Copilot 误用 system。 */
  const currentDb = computed(() => {
    const db = activeTab.value?.db ?? '';
    return db === CONTROL_PLANE_KEY ? '' : db;
  });

  function activateTab(id: string): void {
    if (tabs.value.some((tab) => tab.id === id)) {
      activeTabId.value = id;
    }
  }

  function createTab(options: Partial<SqlConsoleTab> = {}): SqlConsoleTab {
    const ts = now();
    const tab: SqlConsoleTab = {
      id: options.id ?? makeId('sqltab'),
      title: options.title ?? deriveTitle(options.sql ?? '', `SQL ${tabs.value.length + 1}`),
      db: options.db ?? activeTab.value?.db ?? '',
      sql: normalizeStarterSql(options.sql ?? ''),
      results: options.results ?? [],
      summary: options.summary ?? '',
      errorMsg: options.errorMsg ?? '',
      ranOnce: options.ranOnce ?? false,
      source: options.source ?? 'manual',
      createdAt: options.createdAt ?? ts,
      updatedAt: options.updatedAt ?? ts,
    };

    tabs.value.push(tab);
    if (tabs.value.length > MaxTabs) {
      tabs.value.splice(0, tabs.value.length - MaxTabs);
    }
    activeTabId.value = tab.id;
    return tab;
  }

  function closeTab(id: string): void {
    const index = tabs.value.findIndex((tab) => tab.id === id);
    if (index < 0) return;

    if (tabs.value.length === 1) {
      const fresh = defaultTab();
      tabs.value = [fresh];
      activeTabId.value = fresh.id;
      return;
    }

    tabs.value.splice(index, 1);
    if (activeTabId.value === id) {
      const next = tabs.value[Math.min(index, tabs.value.length - 1)];
      activeTabId.value = next?.id ?? null;
    }
  }

  function patchTab(id: string, patch: Partial<Omit<SqlConsoleTab, 'id' | 'createdAt'>>): void {
    const tab = tabs.value.find((item) => item.id === id);
    if (!tab) return;
    const nextPatch = { ...patch };
    if (typeof nextPatch.sql === 'string') {
      nextPatch.sql = normalizeStarterSql(nextPatch.sql);
    }
    Object.assign(tab, nextPatch, { updatedAt: now() });
  }

  function patchActiveTab(patch: Partial<Omit<SqlConsoleTab, 'id' | 'createdAt'>>): void {
    if (!activeTab.value) return;
    patchTab(activeTab.value.id, patch);
  }

  function setCurrent(db: string, sql: string): void {
    patchActiveTab({ db, sql });
  }

  function setTabResults(
    id: string,
    results: SqlConsoleExecutedStatement[],
    summary: string,
    errorMsg = '',
    ranOnce = true,
  ): void {
    patchTab(id, { results, summary, errorMsg, ranOnce });
  }

  function appendResult(
    id: string,
    sql: string,
    result: SqlResultSet,
    source: SqlConsoleExecutedStatement['source'] = 'manual',
  ): void {
    const tab = tabs.value.find((item) => item.id === id);
    if (!tab) return;
    tab.results.push({
      id: makeId('stmt'),
      sql,
      result: normalizeResultSet(result),
      createdAt: now(),
      source,
    });
    tab.ranOnce = true;
    tab.updatedAt = now();
  }

  function clearActiveTab(): void {
    patchActiveTab({
      sql: '',
      results: [],
      errorMsg: '',
      summary: '',
      ranOnce: false,
    });
  }

  function hideControlPlaneForRegularUser(fallbackDb = ''): void {
    const safeFallbackDb = fallbackDb === CONTROL_PLANE_KEY ? '' : fallbackDb;
    for (let i = 0; i < tabs.value.length; i++) {
      const tab = tabs.value[i];
      if (tab.db !== CONTROL_PLANE_KEY) continue;

      Object.assign(tab, {
        title: `SQL ${i + 1}`,
        db: safeFallbackDb,
        sql: defaultDataPlaneSql(safeFallbackDb),
        results: [],
        summary: '',
        errorMsg: '',
        ranOnce: false,
        updatedAt: now(),
      });
    }

    if (pendingExecution.value?.db === CONTROL_PLANE_KEY) {
      pendingExecution.value = null;
    }
  }

  function queueExecution(execution: PendingSqlExecution): void {
    const tab = createTab({
      title: execution.title ?? deriveTitle(execution.sql, `SQL ${tabs.value.length + 1}`),
      db: execution.db,
      sql: execution.sql,
      source: 'manual',
    });
    pendingExecution.value = { ...execution, tabId: tab.id };
  }

  function consumeExecution(): PendingSqlExecution | null {
    const current = pendingExecution.value;
    pendingExecution.value = null;
    return current;
  }

  watch(
    [tabs, activeTabId],
    () => saveState({ tabs: tabs.value, activeTabId: activeTabId.value }),
    { deep: true },
  );

  return {
    tabs,
    activeTabId,
    activeTab,
    pendingExecution,
    currentSql,
    currentDb,
    activateTab,
    createTab,
    closeTab,
    patchTab,
    patchActiveTab,
    setCurrent,
    setTabResults,
    appendResult,
    clearActiveTab,
    hideControlPlaneForRegularUser,
    queueExecution,
    consumeExecution,
  };
});
