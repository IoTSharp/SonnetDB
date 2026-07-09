import { computed, ref, type ComputedRef, type Ref, type WritableComputedRef } from 'vue';
import type { MessageApi } from 'naive-ui';
import {
  execControlPlaneSql,
  execDataSql,
  type SqlResultSet,
} from '@/api/sql';
import { splitSqlStatements } from '@/api/sqlSplit';
import {
  buildClientErrorResultSet,
  buildClientResultSet,
  parseSqlMetaCommand,
} from '@/api/sqlMeta';
import { formatSqlDocument } from '@/api/sqlFormat';
import type { MeasurementInfo } from '@/api/schema';
import type { useAuthStore } from '@/stores/auth';
import type { useConnectionsStore } from '@/stores/connections';
import {
  CONTROL_PLANE_KEY,
  type SqlConsoleExecutedStatement,
  type SqlConsoleTab,
  type useSqlConsoleStore,
} from '@/stores/sqlConsole';
import type { useWorkbenchHistoryStore, WorkbenchHistoryEntry } from '@/stores/workbenchHistory';
import {
  buildCreateDraft,
  buildPreviewPlan,
  buildSelectDraft,
  defaultSqlForDb,
  formatSqlIdentifier,
  isDatabaseCatalogMutating,
  isSchemaMutating,
  makeStatementId,
  normalizeSql,
  quickSqlOptions as buildQuickSqlOptions,
  statementTitle,
  summarizeSqlResult,
  type PlannedStatement,
  type WorkbenchTool,
} from '@/utils/sqlWorkbench';

type AuthStore = ReturnType<typeof useAuthStore>;
type ConnectionsStore = ReturnType<typeof useConnectionsStore>;
type SqlConsoleStore = ReturnType<typeof useSqlConsoleStore>;
type WorkbenchHistoryStore = ReturnType<typeof useWorkbenchHistoryStore>;

export interface SqlExecutionOptions {
  auth: AuthStore;
  connections: ConnectionsStore;
  sqlConsole: SqlConsoleStore;
  workbenchHistory: WorkbenchHistoryStore;
  targetDb: WritableComputedRef<string>;
  sql: WritableComputedRef<string>;
  activeTab: ComputedRef<SqlConsoleTab | null>;
  databases: Ref<string[]>;
  selectedMeasurement: ComputedRef<MeasurementInfo | null>;
  message: MessageApi;
  reloadDbs: () => Promise<void>;
  loadSchema: (db: string, force?: boolean, syncActive?: boolean) => Promise<void>;
  setWorkbenchTool: (tool: WorkbenchTool) => void;
}

export function useSqlExecution(options: SqlExecutionOptions) {
  const {
    auth,
    connections,
    sqlConsole,
    workbenchHistory,
    targetDb,
    sql,
    activeTab,
    databases,
    selectedMeasurement,
    message,
    reloadDbs,
    loadSchema,
    setWorkbenchTool,
  } = options;

  const runningTabId = ref<string | null>(null);
  const previewPlan = ref<ReturnType<typeof buildPreviewPlan> | null>(null);

  const results = computed(() => activeTab.value?.results ?? []);
  const ranOnce = computed(() => activeTab.value?.ranOnce ?? false);
  const running = computed(() => runningTabId.value === activeTab.value?.id);
  const resultSummary = computed(() => activeTab.value?.summary ?? '');
  const errorMsg = computed(() => activeTab.value?.errorMsg ?? '');
  const latestResultItem = computed(() => results.value[results.value.length - 1] ?? null);
  const latestResultSet = computed(() => latestResultItem.value?.result ?? null);

  const previewIsStale = computed(() => {
    if (!previewPlan.value) return false;
    return previewPlan.value.tabId !== activeTab.value?.id
      || previewPlan.value.db !== targetDb.value
      || normalizeSql(sql.value) !== previewPlan.value.statements.map((item) => item.sql).join('\n;\n');
  });

  const quickSqlOptions = computed(() => {
    return buildQuickSqlOptions(auth.isSuperuser, Boolean(selectedMeasurement.value));
  });

  function sqlDraftTitle(): string {
    return activeTab.value?.title ?? `SQL ${sqlConsole.tabs.findIndex((tab) => tab.id === activeTab.value?.id) + 1 || 1}`;
  }

  function createTab(): void {
    const db = defaultDbForNewTab();
    sqlConsole.createTab({
      title: `SQL ${sqlConsole.tabs.length + 1}`,
      db,
      sql: defaultSqlForDb(db, auth.isSuperuser),
    });
    void loadSchema(db);
  }

  function closeTab(id: string): void {
    sqlConsole.closeTab(id);
    void loadSchema(targetDb.value);
  }

  function defaultDbForNewTab(): string {
    if (activeTab.value?.db && activeTab.value.db !== CONTROL_PLANE_KEY) {
      return activeTab.value.db;
    }
    if (databases.value.length > 0) {
      return databases.value[0];
    }
    return auth.isSuperuser ? CONTROL_PLANE_KEY : '';
  }

  function setSqlDraft(sqlText: string): void {
    sql.value = sqlText;
    if (activeTab.value) {
      sqlConsole.patchActiveTab({ source: 'manual' });
    }
  }

  function recordStatementHistory(statement: PlannedStatement, result: SqlResultSet): void {
    const database = targetDb.value === CONTROL_PLANE_KEY ? 'system' : targetDb.value;
    const end = result.end;
    const status = result.error ? 'error' : 'success';
    workbenchHistory.record({
      kind: statement.severity === 'read' ? 'query' : 'operation',
      status,
      title: statementTitle(statement.sql),
      target: database,
      database,
      connectionId: connections.activeProfileId,
      connectionName: connections.activeProfile.name,
      model: statement.meta ? 'console' : 'sql',
      action: statement.label,
      command: statement.sql,
      summary: result.error?.message ?? summarizeSqlResult(result),
      rowCount: end?.rowCount,
      recordsAffected: end && end.recordsAffected >= 0 ? end.recordsAffected : undefined,
      elapsedMs: end?.elapsedMs,
    });
  }

  async function executeStatements(tabId: string, statements: PlannedStatement[]): Promise<void> {
    const collected: SqlConsoleExecutedStatement[] = [];
    let okCount = 0;
    let failCount = 0;
    let totalElapsed = 0;
    let finalErrorMsg = '';

    sqlConsole.patchTab(tabId, {
      errorMsg: '',
      results: [],
      summary: '',
      ranOnce: false,
      title: sqlDraftTitle(),
    });

    if (!statements.length) return;
    if (!targetDb.value) {
      sqlConsole.patchTab(tabId, { errorMsg: '当前没有可执行的数据库。' });
      return;
    }

    runningTabId.value = tabId;
    try {
      for (const statement of statements) {
        const rs = statement.meta
          ? await executeMetaCommand(statement.sql)
          : (targetDb.value === CONTROL_PLANE_KEY
            ? await execControlPlaneSql(auth.api, statement.sql)
            : await execDataSql(auth.api, targetDb.value, statement.sql));

        collected.push({
          id: makeStatementId(),
          sql: statement.sql,
          result: rs,
          createdAt: Date.now(),
          source: statement.meta ? 'meta' : 'manual',
        });
        recordStatementHistory(statement, rs);

        if (rs.error) {
          failCount += 1;
          finalErrorMsg = rs.error.message;
          sqlConsole.setTabResults(tabId, collected, '', rs.error.message, true);
          break;
        }

        okCount += 1;
        if (rs.end) {
          totalElapsed += rs.end.elapsedMs;
        }

        if (!statement.meta && isDatabaseCatalogMutating(statement.sql)) {
          await reloadDbs();
        }
        if (!statement.meta && isSchemaMutating(statement.sql) && targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
          await loadSchema(targetDb.value, true);
        }

        sqlConsole.setTabResults(tabId, [...collected], '', '', true);
      }

      const summaryParts = [
        `共 ${statements.length} 条`,
        `成功 ${okCount}`,
      ];
      if (failCount > 0) {
        summaryParts.push(`失败 ${failCount}`);
      }
      summaryParts.push(`合计 ${totalElapsed.toFixed(2)} ms`);
      const summary = summaryParts.join(' · ');
      sqlConsole.setTabResults(tabId, collected, summary, finalErrorMsg, true);
    } finally {
      if (runningTabId.value === tabId) {
        runningTabId.value = null;
      }
    }
  }

  function cancelPreview(): void {
    previewPlan.value = null;
  }

  async function confirmPreview(): Promise<void> {
    const tab = activeTab.value;
    if (!tab || !previewPlan.value) return;
    if (previewIsStale.value) return;

    const plan = previewPlan.value;
    previewPlan.value = null;
    await executeStatements(tab.id, plan.statements);
  }

  async function run(): Promise<void> {
    const tab = activeTab.value;
    if (!tab) return;

    const statementTexts = splitSqlStatements(sql.value);
    if (statementTexts.length === 0) return;

    const plan = buildPreviewPlan(statementTexts, tab.id, targetDb.value);
    if (plan.writeCount > 0) {
      previewPlan.value = plan;
      return;
    }

    previewPlan.value = null;
    await executeStatements(tab.id, plan.statements);
  }

  async function executeMetaCommand(sqlText: string): Promise<SqlResultSet> {
    const meta = parseSqlMetaCommand(sqlText);
    if (!meta) return buildClientErrorResultSet('console_meta', '未识别的元命令。');

    const currentName = targetDb.value === CONTROL_PLANE_KEY ? 'system' : targetDb.value;

    if (meta.kind === 'current-database') {
      return buildClientResultSet(['current_database'], [[currentName]]);
    }

    const wanted = meta.database;
    const isSystem = wanted === 'system' || wanted === '*';
    if (isSystem) {
      if (!auth.isSuperuser) {
        return buildClientErrorResultSet('forbidden', '仅 superuser 才能切换到系统数据库。');
      }
      targetDb.value = CONTROL_PLANE_KEY;
      return buildClientResultSet(['database'], [['system']]);
    }

    if (!databases.value.includes(wanted)) {
      await reloadDbs();
    }
    if (!databases.value.includes(wanted)) {
      return buildClientErrorResultSet(
        'database_not_found',
        `数据库 "${wanted}" 不存在或当前用户没有访问权限。可用列表：${databases.value.join(', ') || '(空)'}。`,
      );
    }
    targetDb.value = wanted;
    await loadSchema(wanted);
    return buildClientResultSet(['database'], [[wanted]]);
  }

  function clearActiveError(): void {
    if (activeTab.value) {
      sqlConsole.patchActiveTab({ errorMsg: '' });
    }
  }

  function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
    if (entry.model !== 'sql' && entry.model !== 'console') {
      message.info(entry.summary || entry.title);
      return;
    }

    setWorkbenchTool('sql');
    const db = entry.database === 'system' ? CONTROL_PLANE_KEY : entry.database;
    if (db === CONTROL_PLANE_KEY && !auth.isSuperuser) {
      message.warning('当前账号不能打开系统控制面历史。');
      return;
    }
    if (db) {
      targetDb.value = db;
    }
    setSqlDraft(entry.command);
  }

  function applyPendingExecution(): void {
    const pending = sqlConsole.consumeExecution();
    if (!pending) return;

    if (pending.tabId) {
      sqlConsole.activateTab(pending.tabId);
    }
    if (pending.db === CONTROL_PLANE_KEY && !auth.isSuperuser) {
      const fallbackDb = databases.value[0] ?? '';
      targetDb.value = fallbackDb;
      sql.value = defaultSqlForDb(fallbackDb, auth.isSuperuser);
      void loadSchema(fallbackDb);
      return;
    }

    const pendingDb = pending.db;
    targetDb.value = pendingDb;
    sql.value = pending.sql;
    void loadSchema(pendingDb);
    if (pending.runImmediately) {
      void run();
    }
  }

  function explainSql(): void {
    const current = sql.value.trim();
    if (!current) return;
    const explainText = /^explain\b/i.test(current)
      ? current
      : `EXPLAIN ${current.replace(/;+\s*$/u, '')};`;
    setSqlDraft(explainText);
    void run();
  }

  function formatSql(): void {
    const formatted = formatSqlDocument(sql.value);
    if (!formatted.trim()) return;
    setSqlDraft(formatted);
  }

  function onQuickSqlSelect(key: string | number): void {
    const action = String(key);
    if (action === 'show-measurements') {
      setSqlDraft(defaultSqlForDb(targetDb.value, auth.isSuperuser));
      return;
    }

    if (action === 'select-active' && selectedMeasurement.value) {
      setSqlDraft(buildSelectDraft(selectedMeasurement.value, true));
      return;
    }

    if (action === 'describe-active' && selectedMeasurement.value) {
      setSqlDraft(`DESCRIBE MEASUREMENT ${formatSqlIdentifier(selectedMeasurement.value.name)};`);
      return;
    }

    if (action === 'create-active' && selectedMeasurement.value) {
      setSqlDraft(buildCreateDraft(selectedMeasurement.value));
      return;
    }

    if (action === 'show-databases' && auth.isSuperuser) {
      setSqlDraft('SHOW DATABASES;');
      targetDb.value = CONTROL_PLANE_KEY;
      return;
    }

    if (action === 'show-users' && auth.isSuperuser) {
      setSqlDraft('SHOW USERS;');
      targetDb.value = CONTROL_PLANE_KEY;
      return;
    }

    if (action === 'show-grants' && auth.isSuperuser) {
      setSqlDraft('SHOW GRANTS;');
      targetDb.value = CONTROL_PLANE_KEY;
    }
  }

  return {
    previewPlan,
    ranOnce,
    running,
    resultSummary,
    errorMsg,
    latestResultItem,
    latestResultSet,
    previewIsStale,
    quickSqlOptions,
    sqlDraftTitle,
    createTab,
    closeTab,
    setSqlDraft,
    cancelPreview,
    confirmPreview,
    run,
    clearActiveError,
    openHistoryEntry,
    applyPendingExecution,
    explainSql,
    formatSql,
    onQuickSqlSelect,
  };
}
