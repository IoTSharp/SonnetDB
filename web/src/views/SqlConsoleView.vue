<template>
  <div class="workbench-page">
    <header class="workbench-context">
      <div class="workbench-context__main">
        <div class="workbench-context__identity">
          <div class="workbench-context__title-row">
            <n-text class="workbench-context__title">SonnetDB Workbench</n-text>
            <n-text depth="3" class="workbench-context__note">Local Development</n-text>
          </div>
          <n-text depth="3" class="workbench-context__dsn">{{ connectionLabel }}</n-text>
        </div>

        <div class="workbench-context__badges">
          <n-tag
            v-for="badge in accessBadges"
            :key="badge.label"
            size="tiny"
            :type="badge.type"
            :bordered="false"
          >
            {{ badge.label }}
          </n-tag>
        </div>
      </div>
    </header>

    <section class="workbench-frame">
      <aside class="schema-sidebar">
        <div class="schema-toolbar">
          <n-select
            v-model:value="targetDb"
            :options="dbOptions"
            :loading="loadingDbs"
            size="small"
            class="schema-toolbar__select"
            placeholder="database"
          />
          <n-button size="small" quaternary :loading="loadingSchema || loadingDbs" title="Refresh schema" @click="refreshWorkbench">↻</n-button>
          <n-text strong class="schema-toolbar__sql">SQL</n-text>
        </div>

        <div class="schema-search">
          <n-input
            v-model:value="schemaFilter"
            size="small"
            clearable
            placeholder="Search"
          />
        </div>

        <n-scrollbar class="schema-tree">
          <n-alert v-if="targetDb === CONTROL_PLANE_KEY" type="info" :show-icon="false" class="schema-empty-note">
            System database has no measurement schema.
          </n-alert>

          <section v-for="group in explorerGroups" :key="group.key" class="schema-group">
            <button type="button" class="schema-group__head" @click="toggleExplorerGroup(group.key)">
              <span class="schema-group__caret">{{ openGroups[group.key] ? '⌄' : '›' }}</span>
              <span class="schema-group__icon">{{ group.icon }}</span>
              <span class="schema-group__label">{{ group.label }}</span>
              <span class="schema-group__count">{{ group.count }}</span>
            </button>

            <div v-if="openGroups[group.key]" class="schema-group__items">
              <button
                v-for="item in group.items"
                :key="item.key"
                type="button"
                class="schema-item"
                :class="{ 'is-active': activeExplorerKey === item.key }"
                :title="item.meta"
                @click="selectExplorerItem(item)"
                @dblclick="openExplorerItem(item)"
              >
                <span class="schema-item__name">{{ item.name }}</span>
                <span v-if="item.meta" class="schema-item__meta">{{ item.meta }}</span>
              </button>

              <div v-if="group.items.length === 0" class="schema-group__empty">
                {{ group.emptyText }}
              </div>
            </div>
          </section>
        </n-scrollbar>
      </aside>

      <main class="query-workspace">
        <div class="query-tabs">
          <button
            v-for="tab in sqlConsole.tabs"
            :key="tab.id"
            type="button"
            class="query-tab"
            :class="{ 'is-active': tab.id === activeTabId }"
            @click="activeTabId = tab.id"
          >
            <span class="query-tab__icon">SQL</span>
            <span class="query-tab__title">{{ tab.title }}</span>
            <span
              v-if="sqlConsole.tabs.length > 1"
              class="query-tab__close"
              title="Close tab"
              @click.stop="closeTab(tab.id)"
            >×</span>
          </button>
          <button type="button" class="query-tab query-tab--add" title="New SQL tab" @click="createTab">+</button>
        </div>

        <div class="query-toolbar">
          <n-space align="center" :size="8" :wrap="false">
            <n-button size="small" type="primary" :loading="running" @click="run">
              {{ previewPlan ? 'Preview' : 'Run' }}
            </n-button>
            <n-button size="small" @click="explainSql">Explain</n-button>
            <n-button size="small" @click="formatSql">Format</n-button>
            <n-dropdown trigger="click" placement="bottom-start" :options="quickSqlOptions" @select="onQuickSqlSelect">
              <n-button size="small">Quick SQL⌄</n-button>
            </n-dropdown>
            <n-button size="small" disabled title="SonnetDB 当前版本尚未暴露 active process 列表接口">Processes</n-button>
          </n-space>

          <div class="query-toolbar__meta">
            <n-tag size="small" :type="activeTab?.source === 'copilot' ? 'info' : 'default'" :bordered="false">
              {{ activeTab?.source === 'copilot' ? 'Copilot draft' : 'Manual' }}
            </n-tag>
            <n-tag v-if="activeTab?.ranOnce" size="small" type="success" :bordered="false">executed</n-tag>
          </div>
        </div>

        <section class="editor-shell">
          <SqlEditor
            v-model="sql"
            :schema="currentSchema"
            placeholder="SHOW MEASUREMENTS;"
            @cursor="onEditorCursor"
          />
        </section>

        <div class="editor-status">
          <span>search_path: {{ targetDb === CONTROL_PLANE_KEY ? 'system' : (targetDb || 'public') }}</span>
          <span>Ln {{ editorCursor.line }}, Col {{ editorCursor.column }}, Pos {{ editorCursor.position }}/{{ editorCursor.length }}</span>
        </div>

        <section v-if="previewPlan" class="preview-panel">
          <div class="preview-panel__head">
            <div>
              <n-tag size="small" :type="previewPlan.dangerous ? 'error' : 'warning'" :bordered="false">
                {{ previewPlan.dangerous ? 'Dangerous staged preview' : 'Staged preview' }}
              </n-tag>
              <n-text depth="3" class="preview-panel__summary">{{ previewPlan.summary }}</n-text>
            </div>
            <n-button size="small" quaternary @click="cancelPreview">Cancel</n-button>
          </div>

          <div class="preview-panel__body">
            <article
              v-for="(statement, index) in previewPlan.statements"
              :key="`${previewPlan.tabId}:${index}`"
              class="preview-statement"
            >
              <n-tag
                size="tiny"
                :type="statement.severity === 'danger' ? 'error' : statement.severity === 'write' ? 'warning' : 'info'"
                :bordered="false"
              >
                {{ statement.label }}
              </n-tag>
              <code>{{ statement.sql }}</code>
            </article>
          </div>

          <div class="preview-panel__actions">
            <n-checkbox v-if="previewPlan.dangerous" v-model:checked="dangerConfirmed">
              I understand this may modify or delete target data.
            </n-checkbox>
            <n-text v-if="previewIsStale" depth="3">The preview is stale. Run preview again before executing.</n-text>
            <n-button
              size="small"
              type="primary"
              :disabled="previewIsStale || (previewPlan.dangerous && !dangerConfirmed)"
              :loading="running"
              @click="confirmPreview"
            >
              {{ previewPlan.dangerous ? 'Confirm danger run' : 'Confirm run' }}
            </n-button>
          </div>
        </section>

        <section class="result-shell">
          <div class="result-toolbar">
            <n-text class="result-toolbar__timer">{{ resultHeaderText }}</n-text>
            <div class="result-toolbar__actions">
              <n-input
                v-model:value="resultFilter"
                size="small"
                clearable
                placeholder="Search result"
                class="result-search"
              />
              <n-button size="small" quaternary title="Copy visible rows as CSV" :disabled="!latestResultSet?.hasColumns" @click="copyVisibleResults">⧉</n-button>
              <n-button size="small" quaternary title="Export visible rows as CSV" :disabled="!latestResultSet?.hasColumns" @click="downloadVisibleResults">⇩</n-button>
            </div>
          </div>

          <div class="result-grid">
            <n-alert
              v-if="errorMsg && !latestResultSet?.error"
              type="error"
              :title="errorMsg"
              closable
              class="result-alert"
              @close="clearActiveError"
            />
            <n-alert
              v-if="latestResultSet?.error"
              type="error"
              :title="`[${latestResultSet.error.code ?? 'error'}] ${latestResultSet.error.message}`"
              class="result-alert"
            />

            <n-data-table
              v-else-if="latestResultSet?.hasColumns"
              :columns="resultColumns"
              :data="filteredResultRows"
              :row-key="resultRowKey"
              size="small"
              :bordered="false"
              :max-height="420"
            />

            <n-empty v-else-if="ranOnce" description="Statement executed without rows." />
            <n-empty v-else description="Run a SQL statement to see results." />
          </div>

          <div class="result-status">
            <span>{{ executionFooterText }}</span>
            <span>{{ filteredResultRows.length }} rows</span>
          </div>
        </section>
      </main>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { DataTableColumns, DropdownOption } from 'naive-ui';
import {
  NAlert,
  NButton,
  NCheckbox,
  NDataTable,
  NDropdown,
  NEmpty,
  NInput,
  NScrollbar,
  NSpace,
  NSelect,
  NTag,
  NText,
} from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import {
  execControlPlaneSql,
  execDataSql,
  rowsToObjects,
  type SqlResultSet,
} from '@/api/sql';
import { splitSqlStatements } from '@/api/sqlSplit';
import {
  parseSqlMetaCommand,
  buildClientResultSet,
  buildClientErrorResultSet,
} from '@/api/sqlMeta';
import { listDatabases } from '@/api/server';
import { fetchSchema, type MeasurementInfo } from '@/api/schema';
import SqlEditor from '@/components/SqlEditor.vue';
import {
  CONTROL_PLANE_KEY,
  useSqlConsoleStore,
  type SqlConsoleExecutedStatement,
} from '@/stores/sqlConsole';

type StatementSeverity = 'read' | 'write' | 'danger';

interface PlannedStatement {
  sql: string;
  severity: StatementSeverity;
  label: string;
  meta: boolean;
}

interface StagedPreview {
  tabId: string;
  db: string;
  statements: PlannedStatement[];
  queryCount: number;
  writeCount: number;
  dangerCount: number;
  dangerous: boolean;
  summary: string;
}

interface ExplorerItem {
  key: string;
  name: string;
  meta: string;
  kind: 'measurement' | 'function' | 'procedure' | 'placeholder';
  sql?: string;
  measurement?: MeasurementInfo;
}

interface ExplorerGroup {
  key: string;
  label: string;
  icon: string;
  count: number;
  items: ExplorerItem[];
  emptyText: string;
}

interface EditorCursorInfo {
  line: number;
  column: number;
  position: number;
  length: number;
}

interface ResultRow extends Record<string, unknown> {
  __rowIndex: number;
}

interface AccessBadge {
  label: string;
  type: 'default' | 'info' | 'success' | 'warning' | 'error';
}

const auth = useAuthStore();
const sqlConsole = useSqlConsoleStore();

const databases = ref<string[]>([]);
const schema = ref<MeasurementInfo[]>([]);
const schemaFilter = ref('');
const loadingDbs = ref(false);
const loadingSchema = ref(false);
const runningTabId = ref<string | null>(null);
const previewPlan = ref<StagedPreview | null>(null);
const dangerConfirmed = ref(false);
const activeExplorerKey = ref('');
const resultFilter = ref('');
const editorCursor = ref<EditorCursorInfo>({
  line: 1,
  column: 1,
  position: 0,
  length: 0,
});
const openGroups = ref<Record<string, boolean>>({
  tables: true,
  views: true,
  materialized: true,
  functions: true,
  procedures: true,
});

if (!auth.isSuperuser) {
  sqlConsole.hideControlPlaneForRegularUser();
}

const activeTab = computed(() => sqlConsole.activeTab);
const activeTabId = computed({
  get: () => sqlConsole.activeTabId ?? '',
  set: (id: string) => sqlConsole.activateTab(id),
});

const targetDb = computed({
  get: () => activeTab.value?.db ?? '',
  set: (db: string) => sqlConsole.patchActiveTab({ db }),
});

const sql = computed({
  get: () => activeTab.value?.sql ?? '',
  set: (value: string) => {
    sqlConsole.patchActiveTab({ sql: value });
    if (previewPlan.value && previewPlan.value.tabId === activeTab.value?.id) {
      previewPlan.value = null;
      dangerConfirmed.value = false;
    }
  },
});

const results = computed(() => activeTab.value?.results ?? []);
const ranOnce = computed(() => activeTab.value?.ranOnce ?? false);
const running = computed(() => runningTabId.value === activeTab.value?.id);
const currentSchema = computed(() => schema.value);
const resultSummary = computed(() => activeTab.value?.summary ?? '');
const errorMsg = computed(() => activeTab.value?.errorMsg ?? '');

const connectionLabel = computed(() => {
  const host = typeof window !== 'undefined' ? window.location.host : 'localhost';
  const db = targetDb.value === CONTROL_PLANE_KEY ? 'system' : (targetDb.value || 'public');
  return `${host}/${db}`;
});

const accessBadges = computed<AccessBadge[]>(() => {
  if (auth.isSuperuser) {
    return [
      { label: 'explain', type: 'info' },
      { label: 'read', type: 'success' },
      { label: 'execute', type: 'warning' },
      { label: 'export', type: 'default' },
      { label: 'write', type: 'warning' },
      { label: 'ddl', type: 'warning' },
      { label: 'admin', type: 'info' },
    ];
  }

  return [
    { label: 'explain', type: 'info' },
    { label: 'read', type: 'success' },
    { label: 'execute', type: 'warning' },
    { label: 'export', type: 'default' },
    { label: 'write', type: 'warning' },
  ];
});

const dbOptions = computed(() => {
  const options: { label: string; value: string }[] = auth.isSuperuser
    ? [{ label: 'system （系统库 / 控制面）', value: CONTROL_PLANE_KEY }]
    : [];
  return [
    ...options,
    ...databases.value.map((d) => ({ label: d, value: d })),
  ];
});

const filteredMeasurements = computed(() => {
  const keyword = schemaFilter.value.trim().toLowerCase();
  if (!keyword) return schema.value;
  return schema.value.filter((measurement) => {
    if (measurement.name.toLowerCase().includes(keyword)) return true;
    return measurement.columns.some((column) =>
      column.name.toLowerCase().includes(keyword)
      || column.role.toLowerCase().includes(keyword)
      || column.dataType.toLowerCase().includes(keyword));
  });
});

const builtinFunctions = computed<ExplorerItem[]>(() => [
  {
    key: 'fn-time',
    name: 'time()',
    meta: 'windowing helper',
    kind: 'function',
    sql: 'SELECT time(1m) FROM measurement;',
  },
  {
    key: 'fn-knn',
    name: 'knn()',
    meta: 'vector search',
    kind: 'function',
    sql: 'SELECT * FROM measurement ORDER BY knn(vector_col, [0.1, 0.2, 0.3], 5);',
  },
  {
    key: 'fn-cosine',
    name: 'cosine_distance()',
    meta: 'similarity',
    kind: 'function',
    sql: 'SELECT cosine_distance(vec1, vec2) FROM measurement;',
  },
  {
    key: 'fn-l2',
    name: 'l2_distance()',
    meta: 'distance',
    kind: 'function',
    sql: 'SELECT l2_distance(vec1, vec2) FROM measurement;',
  },
]);

const builtinProcedures = computed<ExplorerItem[]>(() => [
  {
    key: 'proc-show-databases',
    name: 'show databases',
    meta: 'control plane',
    kind: 'procedure',
    sql: 'SHOW DATABASES;',
  },
  {
    key: 'proc-show-users',
    name: 'show users',
    meta: 'admin',
    kind: 'procedure',
    sql: 'SHOW USERS;',
  },
  {
    key: 'proc-show-grants',
    name: 'show grants',
    meta: 'admin',
    kind: 'procedure',
    sql: 'SHOW GRANTS;',
  },
]);

const filteredFunctions = computed(() => filterExplorerItems(builtinFunctions.value));
const filteredProcedures = computed(() => filterExplorerItems(builtinProcedures.value));

const explorerGroups = computed<ExplorerGroup[]>(() => [
  {
    key: 'tables',
    label: 'TABLES',
    icon: '▸',
    count: filteredMeasurements.value.length,
    items: filteredMeasurements.value.map((measurement) => ({
      key: measurement.name,
      name: measurement.name,
      meta: `${countColumns(measurement, 'TAG')} TAG · ${countColumns(measurement, 'FIELD')} FIELD · ${measurement.columns.length} cols`,
      kind: 'measurement',
      measurement,
      sql: buildSelectDraft(measurement, false),
    })),
    emptyText: targetDb.value === CONTROL_PLANE_KEY ? 'No tables in system database.' : 'No measurement found.',
  },
  {
    key: 'views',
    label: 'VIEWS',
    icon: '◔',
    count: 0,
    items: [],
    emptyText: 'No views.',
  },
  {
    key: 'materialized',
    label: 'MATERIALIZED',
    icon: '◫',
    count: 0,
    items: [],
    emptyText: 'No materialized views.',
  },
  {
    key: 'functions',
    label: 'FUNCTIONS',
    icon: 'ƒ',
    count: filteredFunctions.value.length,
    items: filteredFunctions.value,
    emptyText: 'No functions.',
  },
  {
    key: 'procedures',
    label: 'PROCEDURES',
    icon: '↦',
    count: filteredProcedures.value.length,
    items: filteredProcedures.value,
    emptyText: 'No procedures.',
  },
]);

const selectedMeasurement = computed(() => {
  if (!schema.value.length) return null;
  const byActive = schema.value.find((measurement) => measurement.name === activeExplorerKey.value);
  return byActive ?? filteredMeasurements.value[0] ?? schema.value[0] ?? null;
});

const latestResultItem = computed(() => results.value[results.value.length - 1] ?? null);
const latestResultSet = computed(() => latestResultItem.value?.result ?? null);
const latestResultRows = computed<ResultRow[]>(() => {
  if (!latestResultSet.value?.hasColumns) return [];
  return rowsToObjects<Record<string, unknown>>(latestResultSet.value)
    .map((row, index) => ({ __rowIndex: index, ...row }));
});

const filteredResultRows = computed(() => {
  const keyword = resultFilter.value.trim().toLowerCase();
  if (!keyword) return latestResultRows.value;
  return latestResultRows.value.filter((row) =>
    Object.values(row).some((value) => stringifyValue(value).includes(keyword)));
});

const resultColumns = computed<DataTableColumns<ResultRow>>(() => {
  const cols = latestResultSet.value?.columns ?? [];
  return cols.map((column) => ({
    title: column,
    key: column,
    ellipsis: { tooltip: true },
    minWidth: Math.max(120, column.length * 10),
  }));
});

const resultHeaderText = computed(() => {
  if (latestResultSet.value?.error) {
    return `Error · ${latestResultSet.value.error.code ?? 'error'}`;
  }
  if (latestResultSet.value?.end) {
    return `Executed in ${latestResultSet.value.end.elapsedMs.toFixed(2)} ms`;
  }
  if (previewPlan.value) {
    return previewPlan.value.summary;
  }
  return resultSummary.value || 'Ready';
});

const executionFooterText = computed(() => {
  if (latestResultSet.value?.error) {
    return latestResultSet.value.error.message;
  }
  if (latestResultSet.value?.end) {
    const parts: string[] = [];
    if (latestResultSet.value.hasColumns) {
      parts.push(`${latestResultSet.value.end.rowCount} rows`);
    }
    if (latestResultSet.value.end.recordsAffected >= 0) {
      parts.push(`affected ${latestResultSet.value.end.recordsAffected}`);
    }
    parts.push(`${latestResultSet.value.end.elapsedMs.toFixed(2)} ms`);
    return parts.join(' · ');
  }
  return ranOnce.value ? (resultSummary.value || 'Statement executed.') : 'Ready';
});

const previewIsStale = computed(() => {
  if (!previewPlan.value) return false;
  return previewPlan.value.tabId !== activeTab.value?.id
    || previewPlan.value.db !== targetDb.value
    || normalizeSql(sql.value) !== previewPlan.value.statements.map((item) => item.sql).join('\n;\n');
});

const quickSqlOptions = computed<DropdownOption[]>(() => {
  const options: DropdownOption[] = [
    { label: 'SHOW MEASUREMENTS', key: 'show-measurements' },
    { label: 'SELECT active measurement', key: 'select-active', disabled: !selectedMeasurement.value },
    { label: 'DESCRIBE active measurement', key: 'describe-active', disabled: !selectedMeasurement.value },
    { label: 'CREATE MEASUREMENT draft', key: 'create-active', disabled: !selectedMeasurement.value },
  ];

  if (auth.isSuperuser) {
    options.unshift(
      { label: 'SHOW DATABASES', key: 'show-databases' },
      { label: 'SHOW USERS', key: 'show-users' },
      { label: 'SHOW GRANTS', key: 'show-grants' },
    );
  }

  return options;
});

function normalizeSql(value: string): string {
  return splitSqlStatements(value).map((stmt) => stmt.trim()).join('\n;\n');
}

function stringifyValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') return value.toLowerCase();
  if (typeof value === 'number' || typeof value === 'bigint' || typeof value === 'boolean') {
    return String(value).toLowerCase();
  }
  if (value instanceof Date) return value.toISOString().toLowerCase();
  try {
    return JSON.stringify(value)?.toLowerCase() ?? String(value).toLowerCase();
  } catch {
    return String(value).toLowerCase();
  }
}

function filterExplorerItems(items: ExplorerItem[]): ExplorerItem[] {
  const keyword = schemaFilter.value.trim().toLowerCase();
  if (!keyword) return items;
  return items.filter((item) =>
    [item.name, item.meta, item.sql ?? '']
      .some((field) => field.toLowerCase().includes(keyword)));
}

function makeStatementId(): string {
  return `stmt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function sqlDraftTitle(): string {
  return activeTab.value?.title ?? `SQL ${sqlConsole.tabs.findIndex((tab) => tab.id === activeTab.value?.id) + 1 || 1}`;
}

function formatSqlIdentifier(name: string): string {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name)
    ? name
    : `"${name.replace(/"/g, '""')}"`;
}

function normalizeFieldType(dataType: string): string {
  const text = dataType.trim().toUpperCase();
  if (!text) return 'FLOAT';
  if (/^(FLOAT|FLOAT32|FLOAT64|DOUBLE|INT|INT32|INT64|BOOL|BOOLEAN|STRING|TEXT|VECTOR\(\d+\))$/.test(text)) {
    return text;
  }
  if (text.includes('VECTOR')) return text;
  return text;
}

function classifyStatement(stmt: string): PlannedStatement {
  const normalized = stmt.trim().replace(/;+\s*$/u, '');
  const meta = parseSqlMetaCommand(normalized);
  if (meta) {
    return {
      sql: normalized,
      severity: 'read',
      label: meta.kind === 'use' ? '元命令 / 切库' : '元命令 / 查询上下文',
      meta: true,
    };
  }

  if (/^(select|show|describe|explain|with)\b/i.test(normalized)) {
    return {
      sql: normalized,
      severity: 'read',
      label: '读取语句',
      meta: false,
    };
  }

  const dangerous = /^(delete|drop|grant|revoke|issue\s+token|create\s+user|drop\s+user|alter\s+user)\b/i.test(normalized);
  return {
    sql: normalized,
    severity: dangerous ? 'danger' : 'write',
    label: dangerous ? '危险写入' : '写操作 / 结构变更',
    meta: false,
  };
}

function buildPreviewPlan(statements: string[], tabId: string, db: string): StagedPreview {
  const planned = statements.map(classifyStatement);
  const queryCount = planned.filter((item) => item.severity === 'read').length;
  const writeCount = planned.filter((item) => item.severity !== 'read').length;
  const dangerCount = planned.filter((item) => item.severity === 'danger').length;
  return {
    tabId,
    db,
    statements: planned,
    queryCount,
    writeCount,
    dangerCount,
    dangerous: dangerCount > 0,
    summary: `${planned.length} 条语句 · ${queryCount} read · ${writeCount} write${dangerCount > 0 ? ` · ${dangerCount} danger` : ''}`,
  };
}

function isSchemaMutating(sqlText: string): boolean {
  return /^(create|drop|alter)\s+measurement\b/i.test(sqlText.trim())
    || /^(create|drop|alter)\s+database\b/i.test(sqlText.trim());
}

function isDatabaseCatalogMutating(sqlText: string): boolean {
  return /^(create|drop)\s+database\b/i.test(sqlText.trim());
}

function countColumns(measurement: MeasurementInfo, role: string): number {
  return measurement.columns.filter((column) => column.role.toUpperCase() === role).length;
}

function refreshWorkbench(): void {
  void reloadDbs();
  if (targetDb.value) {
    void loadSchema(targetDb.value);
  }
}

async function reloadDbs(): Promise<void> {
  loadingDbs.value = true;
  if (activeTab.value) {
    sqlConsole.patchActiveTab({ errorMsg: '' });
  }
  try {
    const result = await listDatabases(auth.api);
    if (result.error) {
      if (activeTab.value) {
        sqlConsole.patchActiveTab({ errorMsg: result.error.message });
      }
      return;
    }
    databases.value = result.databases;
    normalizeTarget();
  } finally {
    loadingDbs.value = false;
  }
}

function normalizeTarget(): void {
  if (auth.isSuperuser) {
    if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY && databases.value.includes(targetDb.value)) {
      return;
    }
    if (databases.value.length > 0) {
      targetDb.value = databases.value[0];
      return;
    }
    targetDb.value = CONTROL_PLANE_KEY;
    return;
  }

  if (targetDb.value && databases.value.includes(targetDb.value)) {
    return;
  }
  targetDb.value = databases.value[0] ?? '';
}

async function loadSchema(db: string): Promise<void> {
  if (!db || db === CONTROL_PLANE_KEY) {
    schema.value = [];
    activeExplorerKey.value = '';
    return;
  }

  loadingSchema.value = true;
  try {
    const resp = await fetchSchema(auth.api, db);
    schema.value = resp.measurements ?? [];
    const current = schema.value.find((measurement) => measurement.name === activeExplorerKey.value);
    activeExplorerKey.value = current?.name ?? schema.value[0]?.name ?? '';
  } catch {
    schema.value = [];
    activeExplorerKey.value = '';
  } finally {
    loadingSchema.value = false;
  }
}

function createTab(): void {
  const db = defaultDbForNewTab();
  sqlConsole.createTab({
    title: `SQL ${sqlConsole.tabs.length + 1}`,
    db,
    sql: defaultSqlForDb(db),
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

function defaultSqlForDb(db: string): string {
  return db === CONTROL_PLANE_KEY && auth.isSuperuser ? 'SHOW DATABASES' : 'SHOW MEASUREMENTS';
}

function setSqlDraft(sqlText: string): void {
  sql.value = sqlText;
  if (activeTab.value) {
    sqlConsole.patchActiveTab({ source: 'manual' });
  }
}

function buildSelectDraft(measurement: MeasurementInfo, sample = false): string {
  const selectColumns = measurement.columns
    .filter((column) => column.name.toLowerCase() !== 'time')
    .map((column) => formatSqlIdentifier(column.name));
  const projection = selectColumns.length > 0
    ? `time, ${selectColumns.join(', ')}`
    : '*';
  const limit = sample ? 20 : 100;
  return [
    `SELECT ${projection}`,
    `FROM ${formatSqlIdentifier(measurement.name)}`,
    `LIMIT ${limit};`,
  ].join('\n');
}

function buildCreateDraft(measurement: MeasurementInfo): string {
  const newName = formatSqlIdentifier(`${measurement.name}_copy`);
  const columns = measurement.columns
    .filter((column) => column.name.toLowerCase() !== 'time')
    .map((column) => {
      const columnName = formatSqlIdentifier(column.name);
      const role = column.role.toUpperCase();
      if (role === 'TAG') {
        return `  ${columnName} TAG`;
      }
      if (role === 'FIELD') {
        return `  ${columnName} FIELD ${normalizeFieldType(column.dataType)}`;
      }
      return `  ${columnName} FIELD ${normalizeFieldType(column.dataType)}`;
    });

  const columnBody = columns.length > 0
    ? columns.join(',\n')
    : '  -- 在此补充 TAG / FIELD';

  return [
    `CREATE MEASUREMENT ${newName} (`,
    columnBody,
    `)`,
    ';',
  ].join('\n');
}

function selectExplorerItem(item: ExplorerItem): void {
  activeExplorerKey.value = item.key;
  if (item.kind !== 'measurement' && item.sql) {
    setSqlDraft(item.sql);
  }
}

function openExplorerItem(item: ExplorerItem): void {
  activeExplorerKey.value = item.key;
  if (item.kind === 'measurement' && item.measurement) {
    setSqlDraft(buildSelectDraft(item.measurement, false));
  } else if (item.sql) {
    setSqlDraft(item.sql);
  }
}

function toggleExplorerGroup(key: string): void {
  openGroups.value[key] = !openGroups.value[key];
}

function onEditorCursor(value: EditorCursorInfo): void {
  editorCursor.value = value;
}

async function executeStatements(tabId: string, statements: PlannedStatement[]): Promise<void> {
  const activeDb = targetDb.value;
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
  if (!activeDb) {
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
        await loadSchema(targetDb.value);
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
  dangerConfirmed.value = false;
}

async function confirmPreview(): Promise<void> {
  const tab = activeTab.value;
  if (!tab || !previewPlan.value) return;
  if (previewIsStale.value) return;
  if (previewPlan.value.dangerous && !dangerConfirmed.value) return;

  const plan = previewPlan.value;
  previewPlan.value = null;
  dangerConfirmed.value = false;
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
    dangerConfirmed.value = false;
    return;
  }

  previewPlan.value = null;
  dangerConfirmed.value = false;
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

function applyPendingExecution(): void {
  const pending = sqlConsole.consumeExecution();
  if (!pending) return;

  if (pending.tabId) {
    sqlConsole.activateTab(pending.tabId);
  }
  if (pending.db === CONTROL_PLANE_KEY && !auth.isSuperuser) {
    const fallbackDb = databases.value[0] ?? '';
    targetDb.value = fallbackDb;
    sql.value = defaultSqlForDb(fallbackDb);
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
  const parts = splitSqlStatements(sql.value);
  if (parts.length === 0) return;
  const formatted = parts.map((statement) => formatSqlStatement(statement)).join('\n\n');
  setSqlDraft(formatted);
}

function formatSqlStatement(statement: string): string {
  const tokens = statement.trim().replace(/;+\s*$/u, '').replace(/\s+/g, ' ');
  if (!tokens) return '';

  const segments = tokens
    .replace(/\b(FROM|WHERE|GROUP BY|ORDER BY|LIMIT|OFFSET|INNER JOIN|LEFT JOIN|RIGHT JOIN|JOIN|VALUES|SET|RETURNING)\b/gi, '\n$1')
    .replace(/,\s*/g, ',\n  ')
    .replace(/\(\s+/g, '(')
    .replace(/\s+\)/g, ')')
    .split('\n')
    .map((line, index) => (index === 0 ? line.trim() : line.trim()));

  return `${segments.join('\n').trim()};`;
}

function onQuickSqlSelect(key: string | number): void {
  const action = String(key);
  if (action === 'show-measurements') {
    setSqlDraft(defaultSqlForDb(targetDb.value));
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

async function copyVisibleResults(): Promise<void> {
  if (!latestResultSet.value?.hasColumns) return;
  const csv = buildCsv(filteredResultRows.value, latestResultSet.value.columns);
  if (!csv) return;
  try {
    await navigator.clipboard.writeText(csv);
  } catch {
    // ignore
  }
}

function downloadVisibleResults(): void {
  if (!latestResultSet.value?.hasColumns) return;
  const csv = buildCsv(filteredResultRows.value, latestResultSet.value.columns);
  if (!csv) return;
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `${sqlDraftTitle().replace(/\s+/g, '_') || 'result'}.csv`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

function buildCsv(rows: ResultRow[], columns: string[]): string {
  const escape = (value: unknown): string => {
    const text = value === null || value === undefined ? '' : String(value);
    const normalized = text.replace(/\r?\n/g, ' ');
    return /[",\n]/.test(normalized) ? `"${normalized.replace(/"/g, '""')}"` : normalized;
  };

  const lines = [
    columns.map(escape).join(','),
    ...rows.map((row) => columns.map((column) => escape(row[column])).join(',')),
  ];
  return `${lines.join('\n')}\n`;
}

function resultRowKey(row: ResultRow): number {
  return row.__rowIndex;
}

watch(targetDb, (db) => {
  void loadSchema(db);
  if (previewPlan.value && previewPlan.value.db !== db) {
    previewPlan.value = null;
    dangerConfirmed.value = false;
  }
}, { immediate: false });

watch(activeTabId, () => {
  cancelPreview();
  resultFilter.value = '';
});

watch(
  () => sqlConsole.pendingExecution,
  () => {
    if (sqlConsole.pendingExecution) {
      applyPendingExecution();
    }
  },
  { deep: true },
);

onMounted(async () => {
  await reloadDbs();
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    await loadSchema(targetDb.value);
  }
  applyPendingExecution();
});
</script>

<style scoped>
.workbench-page {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.workbench-toolbar {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 14px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 10px;
  background: #fff;
  box-shadow: none;
}

.workbench-toolbar__main {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.workbench-toolbar__title {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--sndb-ink-strong);
}

.workbench-toolbar__subtitle,
.workbench-toolbar__hint,
.editor-hint {
  font-size: 12px;
}

.workbench-toolbar__meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.workbench-grid {
  display: grid;
  grid-template-columns: minmax(320px, 360px) minmax(0, 1fr);
  gap: 16px;
  align-items: start;
  min-height: calc(100vh - 188px);
}

.workbench-tab {
  padding-top: 16px;
}

.workbench-main,
.workbench-explorer {
  min-width: 0;
}

.workbench-main {
  align-self: start;
}

.panel-card {
  border-radius: 10px;
  box-shadow: none;
  border: 1px solid rgba(13, 59, 102, 0.08);
}

.panel-card--editor,
.panel-card--preview {
  margin-top: 12px;
}

.panel-card--schema {
  overflow: hidden;
}

.schema-panel {
  min-width: 0;
}

.schema-panel__header {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border-bottom: 1px solid rgba(13, 59, 102, 0.08);
  background: rgba(248, 251, 255, 0.68);
}

.schema-panel__title-block {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.schema-panel__title {
  display: block;
  line-height: 1.25;
  white-space: nowrap;
}

.schema-panel__subtitle {
  display: block;
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.schema-panel__tools {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
}

.schema-filter {
  width: 100%;
  min-width: 0;
}

.schema-panel__body {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
  padding: 12px 14px 14px;
}

.schema-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.schema-scroll {
  max-height: calc(100vh - 344px);
  padding-right: 4px;
}

.measurement-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px 10px 12px;
  margin-bottom: 10px;
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 10px;
  background: #fff;
}

.measurement-card__head {
  display: flex;
  align-items: start;
  justify-content: space-between;
  gap: 10px;
}

.measurement-card__identity {
  flex: 1 1 auto;
  min-width: 0;
}

.measurement-card__name {
  display: block;
  overflow: hidden;
  line-height: 1.35;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.measurement-card__actions {
  flex: 0 0 auto;
  justify-content: flex-end;
}

.measurement-card__meta {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 4px;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.measurement-card__columns {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.preview-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
}

.preview-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.preview-item {
  padding: 10px 12px;
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 8px;
  background: rgba(248, 251, 255, 0.7);
}

.preview-item__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 8px;
}

.preview-item__index {
  font-size: 12px;
}

.preview-item__sql {
  display: block;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.6;
  color: var(--sndb-ink-strong);
}

@media (max-width: 1120px) {
  .workbench-grid {
    grid-template-columns: 1fr;
    min-height: auto;
  }

  .schema-scroll {
    max-height: 380px;
  }

}

@media (max-width: 840px) {
  .workbench-toolbar__main {
    flex-direction: column;
    align-items: stretch;
  }

  .workbench-toolbar__meta {
    align-items: flex-start;
  }
}

.workbench-page {
  display: flex;
  flex-direction: column;
  gap: 10px;
  height: calc(100vh - 96px);
  min-height: 680px;
  overflow: hidden;
}

.workbench-context {
  flex: 0 0 auto;
  padding: 0 2px 2px;
}

.workbench-context__main {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.workbench-context__identity {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.workbench-context__title-row {
  display: flex;
  align-items: baseline;
  gap: 12px;
  min-width: 0;
}

.workbench-context__title {
  color: var(--sndb-ink-strong);
  font-size: 18px;
  font-weight: 700;
}

.workbench-context__note,
.workbench-context__dsn {
  font-size: 12px;
}

.workbench-context__dsn {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workbench-context__badges {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  justify-content: flex-end;
  padding-top: 2px;
}

.workbench-frame {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: 250px minmax(0, 1fr);
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 8px;
  background: #fff;
  overflow: hidden;
}

.schema-sidebar {
  display: flex;
  flex-direction: column;
  min-width: 0;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.schema-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 10px 8px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.schema-toolbar__select {
  flex: 1;
  min-width: 0;
}

.schema-toolbar__sql {
  color: var(--sndb-ink-soft);
  font-size: 12px;
  letter-spacing: 0.04em;
}

.schema-search {
  padding: 8px 10px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.schema-tree {
  flex: 1;
  min-height: 0;
  padding: 8px 8px 10px 10px;
}

.schema-empty-note {
  margin: 0 2px 8px;
}

.schema-group {
  margin-bottom: 8px;
}

.schema-group__head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 7px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--sndb-ink-strong);
  font: inherit;
  cursor: pointer;
  text-align: left;
}

.schema-group__head:hover {
  background: rgba(44, 123, 229, 0.06);
}

.schema-group__caret {
  width: 10px;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.schema-group__icon {
  width: 14px;
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  text-align: center;
}

.schema-group__label {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.04em;
}

.schema-group__count {
  min-width: 20px;
  padding: 0 6px;
  border-radius: 999px;
  background: rgba(44, 123, 229, 0.08);
  color: rgb(44, 123, 229);
  font-size: 11px;
  line-height: 18px;
  text-align: center;
}

.schema-group__items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 4px 0 0 20px;
}

.schema-item {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 7px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
  text-align: left;
}

.schema-item:hover {
  background: rgba(44, 123, 229, 0.06);
}

.schema-item.is-active {
  background: rgba(44, 123, 229, 0.13);
}

.schema-item__name {
  display: block;
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 13px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.schema-item__meta {
  display: block;
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-soft);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.schema-group__empty {
  padding: 6px 8px;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.query-workspace {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.query-tabs {
  display: flex;
  align-items: stretch;
  gap: 1px;
  overflow-x: auto;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #f8fbff;
}

.query-tab {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex: 0 0 auto;
  padding: 10px 14px;
  border: 0;
  border-right: 1px solid rgba(15, 23, 42, 0.06);
  background: transparent;
  color: #567;
  font: inherit;
  cursor: pointer;
}

.query-tab.is-active {
  background: #fff;
  color: #1f2a44;
  box-shadow: inset 0 -2px 0 rgb(44, 123, 229);
}

.query-tab__icon {
  padding: 1px 5px;
  border-radius: 3px;
  background: rgba(44, 123, 229, 0.08);
  color: rgb(44, 123, 229);
  font-size: 11px;
  font-weight: 700;
}

.query-tab.is-active .query-tab__icon {
  background: rgba(44, 123, 229, 0.12);
}

.query-tab__title {
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.query-tab__close {
  margin-left: 2px;
  color: #789;
  font-size: 16px;
  line-height: 1;
}

.query-tab--add {
  width: 40px;
  justify-content: center;
  padding: 0;
  font-size: 20px;
  font-weight: 300;
}

.query-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.query-toolbar__meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.editor-shell {
  flex: 0 0 340px;
  min-height: 260px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.editor-shell :deep(.sql-editor) {
  height: 100%;
}

.editor-status {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fafcff;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.preview-panel {
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffef8;
}

.preview-panel__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 8px;
}

.preview-panel__summary {
  display: block;
  margin-top: 4px;
  font-size: 12px;
}

.preview-panel__body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.preview-statement {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
}

.preview-statement code {
  display: block;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.preview-panel__actions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-top: 10px;
}

.result-shell {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.result-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.result-toolbar__timer {
  font-size: 12px;
  color: #345;
}

.result-toolbar__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.result-search {
  width: 240px;
}

.result-grid {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.result-grid :deep(.n-data-table) {
  border-radius: 0;
}

.result-grid :deep(.n-empty) {
  margin: 24px;
}

.result-alert {
  margin: 12px;
}

.result-status {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  background: #fafcff;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

@media (max-width: 1280px) {
  .workbench-frame {
    grid-template-columns: 1fr;
  }

  .schema-sidebar {
    border-right: 0;
    border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  }

  .editor-shell {
    flex-basis: 300px;
  }
}

@media (max-width: 840px) {
  .workbench-page {
    height: auto;
    min-height: 0;
  }

  .workbench-context__main,
  .query-toolbar,
  .result-toolbar {
    flex-direction: column;
    align-items: stretch;
  }

  .result-search {
    width: 100%;
  }

  .workbench-context__badges {
    justify-content: flex-start;
  }
}
</style>
