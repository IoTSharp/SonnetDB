<template>
  <div class="workbench-page">
    <header class="workbench-context">
      <div class="workbench-context__main">
        <div class="workbench-context__identity">
          <div class="workbench-context__title-row">
            <n-text class="workbench-context__title">SonnetDB Studio</n-text>
            <n-text depth="3" class="workbench-context__note">Local Development</n-text>
          </div>
          <n-text depth="3" class="workbench-context__dsn">{{ connectionLabel }}</n-text>
        </div>

        <div class="workbench-context__tools">
          <div class="workbench-mode-switch" role="tablist" aria-label="Studio mode">
            <button
              type="button"
              class="workbench-mode-switch__button"
              :class="{ 'is-active': activeWorkbenchTool === 'sql' }"
              @click="setWorkbenchTool('sql')"
            >
              SQL
            </button>
            <button
              type="button"
              class="workbench-mode-switch__button"
              :class="{ 'is-active': activeWorkbenchTool === 'trajectory' }"
              @click="setWorkbenchTool('trajectory')"
            >
              Trajectory
            </button>
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
      </div>
    </header>

    <section class="workbench-frame">
      <aside class="schema-sidebar">
        <div class="schema-toolbar">
          <div v-if="auth.isSuperuser" class="schema-toolbar__create">
            <n-button size="small" type="primary" :loading="databaseActionBusy" @click="openCreateDatabaseDialog">
              Create
            </n-button>
            <n-popconfirm
              :show-icon="false"
              :positive-button-props="{ type: 'error' }"
              :negative-button-props="{ tertiary: true }"
              :disabled="!canDropDatabase"
              @positive-click="dropActiveDatabase"
            >
              <template #trigger>
                <n-button size="small" tertiary type="error" :disabled="!canDropDatabase || databaseActionBusy">
                  Drop
                </n-button>
              </template>
              <span>Delete database {{ targetDb === CONTROL_PLANE_KEY ? 'system' : (targetDb || '(none)') }}?</span>
            </n-popconfirm>
          </div>

          <div class="schema-toolbar__row">
            <n-button size="small" quaternary :loading="loadingSchema || loadingDbs" title="Refresh databases" @click="refreshWorkbench">↻</n-button>
            <n-input
              v-model:value="schemaFilter"
              size="small"
              clearable
              placeholder="Search databases / measurements"
              class="schema-toolbar__search"
            />
          </div>
        </div>

        <n-modal
          v-model:show="showCreateDatabaseDialog"
          :mask-closable="!databaseActionBusy"
          :close-on-esc="!databaseActionBusy"
        >
          <n-card
            title="Create database"
            :bordered="false"
            size="small"
            class="create-database-dialog"
            role="dialog"
            aria-modal="true"
          >
            <n-space vertical :size="12">
              <n-text depth="3">Enter a valid database name, then confirm to create it.</n-text>
              <n-input
                v-model:value="newDatabaseName"
                size="small"
                clearable
                placeholder="new database"
                autofocus
                @keyup.enter="createDatabase"
              />
              <n-space justify="end">
                <n-button tertiary :disabled="databaseActionBusy" @click="closeCreateDatabaseDialog">
                  Cancel
                </n-button>
                <n-button
                  type="primary"
                  :loading="databaseActionBusy"
                  :disabled="!canCreateDatabase"
                  @click="createDatabase"
                >
                  Create
                </n-button>
              </n-space>
            </n-space>
          </n-card>
        </n-modal>

        <n-scrollbar class="schema-tree">
          <n-alert v-if="databaseTree.length === 0" type="info" :show-icon="false" class="schema-empty-note">
            {{ databases.length === 0 ? 'No databases available yet.' : 'No databases match this filter.' }}
          </n-alert>

          <section class="schema-group schema-group--databases">
            <button type="button" class="schema-group__head" @click="toggleGroup('databases')">
              <span class="schema-group__caret">{{ openGroups.databases ? '⌄' : '›' }}</span>
              <span class="schema-group__icon">◫</span>
              <span class="schema-group__label">DATABASES</span>
              <span class="schema-group__count">{{ databaseTree.length }}</span>
            </button>

            <div v-if="openGroups.databases" class="schema-group__items">
              <button
                v-if="auth.isSuperuser && systemTreeNode"
                type="button"
                class="schema-item schema-item--database"
                :class="{ 'is-active': targetDb === CONTROL_PLANE_KEY }"
                :title="systemTreeNode.meta"
                @click="selectDatabase(CONTROL_PLANE_KEY)"
              >
                <span class="schema-item__name">{{ systemTreeNode.name }}</span>
                <span class="schema-item__meta">{{ systemTreeNode.meta }}</span>
              </button>

              <div
                v-for="dbNode in databaseTree"
                :key="dbNode.name"
                class="schema-database-node"
              >
                <button
                  type="button"
                  class="schema-item schema-item--database"
                  :class="{ 'is-active': targetDb === dbNode.name }"
                  :title="dbNode.meta"
                  @click="selectDatabase(dbNode.name)"
                >
                  <span
                    class="schema-item__caret"
                    @click.stop="toggleDatabaseExpansion(dbNode.name)"
                  >{{ expandedDatabases[dbNode.name] ? '⌄' : '›' }}</span>
                  <span class="schema-item__name">{{ dbNode.name }}</span>
                  <span class="schema-item__meta">{{ dbNode.meta }}</span>
                </button>

                <div v-if="expandedDatabases[dbNode.name]" class="schema-group__items schema-group__items--children">
                  <div v-if="dbNode.measurements.length > 0" class="schema-child-block">
                    <div class="schema-child-block__head">
                      <span>Measurements</span>
                      <span>{{ dbNode.measurements.length }}</span>
                    </div>
                    <button
                      v-for="measurement in dbNode.measurements"
                      :key="`${dbNode.name}:measurement:${measurement.name}`"
                      type="button"
                      class="schema-item schema-item--measurement"
                      :class="{ 'is-active': targetDb === dbNode.name && activeExplorerKey === measurement.name }"
                      :title="measurementMeta(measurement)"
                      @click="selectMeasurement(dbNode.name, measurement)"
                      @dblclick="openMeasurement(measurement)"
                    >
                      <span class="schema-item__name">{{ measurement.name }}</span>
                      <span class="schema-item__meta">{{ measurementMeta(measurement) }}</span>
                    </button>
                  </div>

                  <div v-if="dbNode.tables.length > 0" class="schema-child-block">
                    <div class="schema-child-block__head">
                      <span>Tables</span>
                      <span>{{ dbNode.tables.length }}</span>
                    </div>
                    <button
                      v-for="table in dbNode.tables"
                      :key="`${dbNode.name}:table:${table.name}`"
                      type="button"
                      class="schema-item schema-item--table"
                      :class="{ 'is-active': targetDb === dbNode.name && activeExplorerKey === `table:${table.name}` }"
                      :title="tableMeta(table)"
                      @click="selectTable(dbNode.name, table)"
                      @dblclick="openTable(table)"
                    >
                      <span class="schema-item__name">{{ table.name }}</span>
                      <span class="schema-item__meta">{{ tableMeta(table) }}</span>
                    </button>
                  </div>

                  <div v-if="dbNode.documents.length > 0" class="schema-child-block">
                    <div class="schema-child-block__head">
                      <span>Documents</span>
                      <span>{{ dbNode.documents.length }}</span>
                    </div>
                    <button
                      v-for="collection in dbNode.documents"
                      :key="`${dbNode.name}:document:${collection.name}`"
                      type="button"
                      class="schema-item schema-item--document"
                      :class="{ 'is-active': targetDb === dbNode.name && activeExplorerKey === `document:${collection.name}` }"
                      :title="documentCollectionMeta(collection)"
                      @click="selectDocumentCollection(dbNode.name, collection)"
                      @dblclick="openDocumentCollection(collection)"
                    >
                      <span class="schema-item__name">{{ collection.name }}</span>
                      <span class="schema-item__meta">{{ documentCollectionMeta(collection) }}</span>
                    </button>
                  </div>

                  <div v-if="dbNode.indexes.length > 0" class="schema-child-block">
                    <div class="schema-child-block__head">
                      <span>Indexes</span>
                      <span>{{ dbNode.indexes.length }}</span>
                    </div>
                    <button
                      v-for="index in dbNode.indexes"
                      :key="`${dbNode.name}:index:${index.id}`"
                      type="button"
                      class="schema-item schema-item--index"
                      :class="{ 'is-active': targetDb === dbNode.name && activeExplorerKey === index.id }"
                      :title="indexMeta(index)"
                      @click="selectIndex(dbNode.name, index)"
                      @dblclick="showIndex(index)"
                    >
                      <span class="schema-item__name">{{ index.name }}</span>
                      <span class="schema-item__meta">{{ index.owner }} · {{ indexMeta(index) }}</span>
                    </button>
                  </div>

                  <div v-if="dbNode.backupStatus" class="schema-child-block">
                    <div class="schema-child-block__head">
                      <span>Backup</span>
                      <span>{{ dbNode.backupStatus.hasRestoreManifest ? 'manifest' : 'status' }}</span>
                    </div>
                    <button
                      type="button"
                      class="schema-item schema-item--backup"
                      :class="{ 'is-active': targetDb === dbNode.name && activeExplorerKey === 'backup-status' }"
                      :title="backupMeta(dbNode.backupStatus)"
                      @click="selectBackupStatus(dbNode.name)"
                    >
                      <span class="schema-item__name">backup status</span>
                      <span class="schema-item__meta">{{ backupMeta(dbNode.backupStatus) }}</span>
                    </button>
                  </div>

                  <div v-if="dbNode.measurements.length === 0 && dbNode.tables.length === 0 && dbNode.documents.length === 0 && dbNode.indexes.length === 0" class="schema-group__empty">
                    {{ dbNode.emptyText }}
                  </div>
                </div>
              </div>
            </div>
          </section>
        </n-scrollbar>

        <section class="maintenance-panel">
          <div class="maintenance-panel__head">
            <span>Maintenance</span>
            <span>{{ targetDb === CONTROL_PLANE_KEY ? 'system' : (targetDb || 'none') }}</span>
          </div>
          <div class="maintenance-panel__actions">
            <n-button size="tiny" tertiary :loading="maintenanceBusy === 'health_check'" :disabled="!targetDb || targetDb === CONTROL_PLANE_KEY" @click="runHealthCheck">
              Health
            </n-button>
            <n-button size="tiny" tertiary :loading="maintenanceBusy.startsWith('rebuild:')" :disabled="!selectedIndex" @click="rebuildSelectedIndex">
              Rebuild
            </n-button>
          </div>
          <n-input
            v-model:value="maintenanceBackupDirectory"
            size="tiny"
            clearable
            placeholder="Backup directory"
          />
          <n-input
            v-model:value="maintenanceRestoreTargetDirectory"
            size="tiny"
            clearable
            placeholder="Restore dry-run target"
          />
          <div class="maintenance-panel__actions">
            <n-button size="tiny" tertiary :loading="maintenanceBusy === 'backup_verify'" :disabled="!maintenanceBackupDirectory.trim() || !auth.isSuperuser" @click="verifyBackup">
              Verify
            </n-button>
            <n-button size="tiny" tertiary :loading="maintenanceBusy === 'restore_dry_run'" :disabled="!maintenanceBackupDirectory.trim() || !maintenanceRestoreTargetDirectory.trim() || !auth.isSuperuser" @click="restoreDryRun">
              Dry-run
            </n-button>
          </div>
          <p v-if="maintenanceLastResult" class="maintenance-panel__result">
            {{ maintenanceLastResult.status }} · {{ maintenanceLastResult.message }}
          </p>
        </section>
      </aside>

      <main class="query-workspace">
        <template v-if="activeWorkbenchTool === 'sql'">
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
                <n-button size="small" quaternary title="Copy result set as CSV" :disabled="!latestResultSet?.hasColumns" @click="copyVisibleResults">⧉</n-button>
                <n-button size="small" quaternary title="Export result set as CSV" :disabled="!latestResultSet?.hasColumns" @click="downloadVisibleResults">⇩</n-button>
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

              <SqlResultPanel
                v-else-if="latestResultSet?.hasColumns"
                class="result-panel"
                :index="0"
                :sql="latestResultItem?.sql ?? ''"
                :result="latestResultSet"
                :display-rows="displayedResultRows"
              />

              <n-empty v-else-if="ranOnce" description="Statement executed without rows." />
              <n-empty v-else description="Run a SQL statement to see results." />
            </div>

            <div class="result-status">
              <span>{{ executionFooterText }}</span>
              <span>{{ filteredResultRows.length }} rows</span>
            </div>
          </section>
        </template>

        <TrajectoryMap
          v-else
          class="trajectory-workbench"
          :embedded="true"
          :initial-db="trajectoryInitialDb"
          :initial-measurement="trajectoryInitialMeasurement"
        />
      </main>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import type { DropdownOption } from 'naive-ui';
import {
  NAlert,
  NButton,
  NCheckbox,
  NCard,
  NDropdown,
  NEmpty,
  NInput,
  NModal,
  NPopconfirm,
  NScrollbar,
  NSpace,
  NTag,
  NText,
  useMessage,
} from 'naive-ui';
import { useAuthStore } from '@/stores/auth';
import {
  execControlPlaneSql,
  execDataSql,
  isValidIdentifier,
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
import {
  fetchSchema,
  runMaintenance,
  type BackupStatusInfo,
  type DocumentCollectionInfo,
  type IndexLifecycleInfo,
  type MaintenanceResponse,
  type MeasurementInfo,
  type SchemaResponse,
  type TableInfo,
} from '@/api/schema';
import { formatSqlDocument } from '@/api/sqlFormat';
import { formatSqlValue } from '@/utils/sqlValue';
import SqlEditor from '@/components/SqlEditor.vue';
import SqlResultPanel from '@/components/SqlResultPanel.vue';
import TrajectoryMap from '@/views/TrajectoryMap.vue';
import {
  CONTROL_PLANE_KEY,
  useSqlConsoleStore,
  type SqlConsoleExecutedStatement,
} from '@/stores/sqlConsole';

type WorkbenchTool = 'sql' | 'trajectory';
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

interface DatabaseTreeNode {
  name: string;
  meta: string;
  measurements: MeasurementInfo[];
  tables: TableInfo[];
  documents: DocumentCollectionInfo[];
  indexes: IndexLifecycleInfo[];
  backupStatus: BackupStatusInfo | null;
  loading: boolean;
  error: string;
  emptyText: string;
}

interface SystemTreeNode {
  name: string;
  meta: string;
}

const auth = useAuthStore();
const sqlConsole = useSqlConsoleStore();
const route = useRoute();
const router = useRouter();
const message = useMessage();

const databases = ref<string[]>([]);
const schema = ref<MeasurementInfo[]>([]);
const schemaByDb = ref<Record<string, SchemaResponse>>({});
const schemaLoadingByDb = ref<Record<string, boolean>>({});
const schemaErrorByDb = ref<Record<string, string>>({});
const schemaFilter = ref('');
const maintenanceBackupDirectory = ref('');
const maintenanceRestoreTargetDirectory = ref('');
const maintenanceBusy = ref('');
const maintenanceLastResult = ref<MaintenanceResponse | null>(null);
const newDatabaseName = ref('');
const showCreateDatabaseDialog = ref(false);
const databaseActionBusy = ref(false);
const expandedDatabases = ref<Record<string, boolean>>({});
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
  databases: true,
  measurements: true,
  views: true,
  materialized: true,
  methods: true,
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
const currentSchemaResponse = computed<SchemaResponse | null>(() => {
  const db = targetDb.value;
  if (!db || db === CONTROL_PLANE_KEY) return null;
  return schemaByDb.value[db] ?? null;
});
const currentSchema = computed(() => currentSchemaResponse.value?.measurements ?? schema.value);
const resultSummary = computed(() => activeTab.value?.summary ?? '');
const errorMsg = computed(() => activeTab.value?.errorMsg ?? '');

const activeWorkbenchTool = computed<WorkbenchTool>(() =>
  route.query.tool === 'trajectory' ? 'trajectory' : 'sql');

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

function setWorkbenchTool(tool: WorkbenchTool): void {
  if (activeWorkbenchTool.value === tool) return;
  void router.replace({
    name: 'sql',
    query: tool === 'trajectory' ? { tool: 'trajectory' } : {},
  });
}

const databaseTree = computed<DatabaseTreeNode[]>(() => {
  const keyword = schemaFilter.value.trim().toLowerCase();
  return databases.value.flatMap((name) => {
    const dbSchema = schemaByDb.value[name];
    const measurements = dbSchema?.measurements ?? [];
    const tables = dbSchema?.tables ?? [];
    const documents = dbSchema?.documentCollections ?? [];
    const indexes = dbSchema?.indexes ?? [];
    const backupStatus = dbSchema?.backupStatus ?? null;
    const loaded = hasCachedSchema(name);
    const loading = Boolean(schemaLoadingByDb.value[name]);
    const error = schemaErrorByDb.value[name] ?? '';
    const dbMatches = !keyword || name.toLowerCase().includes(keyword);
    const filteredMeasurements = !keyword || dbMatches
      ? measurements
      : measurements.filter((measurement) => measurementMatchesFilter(measurement, keyword));
    const filteredTables = !keyword || dbMatches
      ? tables
      : tables.filter((table) => tableMatchesFilter(table, keyword));
    const filteredDocuments = !keyword || dbMatches
      ? documents
      : documents.filter((collection) => documentCollectionMatchesFilter(collection, keyword));
    const filteredIndexes = !keyword || dbMatches
      ? indexes
      : indexes.filter((index) => indexMatchesFilter(index, keyword));

    if (keyword && !dbMatches
      && filteredMeasurements.length === 0
      && filteredTables.length === 0
      && filteredDocuments.length === 0
      && filteredIndexes.length === 0) {
      return [];
    }

    return [{
      name,
      meta: databaseMeta(loaded, loading, error, measurements.length, tables.length, documents.length, indexes.length),
      measurements: filteredMeasurements,
      tables: filteredTables,
      documents: filteredDocuments,
      indexes: filteredIndexes,
      backupStatus,
      loading,
      error,
      emptyText: databaseEmptyText(loaded, loading, error, keyword),
    }];
  });
});

const systemTreeNode = computed<SystemTreeNode | null>(() => {
  if (!auth.isSuperuser) return null;
  const keyword = schemaFilter.value.trim().toLowerCase();
  if (keyword && !'system control plane'.includes(keyword)) return null;
  return { name: 'system', meta: 'control plane' };
});

const canCreateDatabase = computed(() => {
  const name = newDatabaseName.value.trim();
  return auth.isSuperuser
    && !databaseActionBusy.value
    && isValidIdentifier(name)
    && !databases.value.includes(name);
});

const canDropDatabase = computed(() =>
  auth.isSuperuser
  && !databaseActionBusy.value
  && targetDb.value.length > 0
  && targetDb.value !== CONTROL_PLANE_KEY
  && databases.value.includes(targetDb.value));

const trajectoryInitialDb = computed(() => {
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) return targetDb.value;
  return databases.value[0] ?? '';
});

const trajectoryInitialMeasurement = computed(() => {
  if (!trajectoryInitialDb.value) return '';
  const measurements = schemaByDb.value[trajectoryInitialDb.value]?.measurements ?? [];
  const active = measurements.find((measurement) => measurement.name === activeExplorerKey.value);
  if (active?.columns.some(isGeoField)) return active.name;
  return measurements.find((measurement) => measurement.columns.some(isGeoField))?.name ?? '';
});

const selectedMeasurement = computed(() => {
  if (!schema.value.length) return null;
  const byActive = schema.value.find((measurement) => measurement.name === activeExplorerKey.value);
  return byActive ?? schema.value[0] ?? null;
});

const selectedIndex = computed(() => {
  const indexes = currentSchemaResponse.value?.indexes ?? [];
  return indexes.find((index) => index.id === activeExplorerKey.value) ?? null;
});

const latestResultItem = computed(() => results.value[results.value.length - 1] ?? null);
const latestResultSet = computed(() => latestResultItem.value?.result ?? null);
const latestResultRows = computed<ResultRow[]>(() => {
  if (!latestResultSet.value?.hasColumns) return [];
  return rowsToObjects<Record<string, unknown>>(latestResultSet.value)
    .map((row, index) => ({ __rowIndex: index, ...row }));
});

const filteredResultRows = computed<ResultRow[]>(() => {
  const keyword = resultFilter.value.trim().toLowerCase();
  if (!keyword) return latestResultRows.value;
  return latestResultRows.value.filter((row) =>
    Object.values(row).some((value) => stringifyValue(value).includes(keyword)));
});

const displayedResultRows = computed(() => {
  if (!latestResultSet.value?.hasColumns) return [];
  const columns = latestResultSet.value.columns;
  return filteredResultRows.value.map((row) => columns.map((column) => row[column]));
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

function hasCachedSchema(db: string): boolean {
  return Object.prototype.hasOwnProperty.call(schemaByDb.value, db);
}

function normalizeActiveExplorerKey(dbSchema: SchemaResponse): string {
  const key = activeExplorerKey.value;
  if (!key) return dbSchema.measurements?.[0]?.name ?? '';
  if (dbSchema.measurements?.some((measurement) => measurement.name === key)) return key;
  if (dbSchema.tables?.some((table) => `table:${table.name}` === key)) return key;
  if (dbSchema.documentCollections?.some((collection) => `document:${collection.name}` === key)) return key;
  if (dbSchema.indexes?.some((index) => index.id === key)) return key;
  if (key === 'backup-status' && dbSchema.backupStatus) return key;
  return dbSchema.measurements?.[0]?.name ?? '';
}

function isGeoField(column: { role: string; dataType: string }): boolean {
  return column.role.toLowerCase() === 'field' && column.dataType.toLowerCase() === 'geopoint';
}

function measurementMatchesFilter(measurement: MeasurementInfo, keyword: string): boolean {
  return measurement.name.toLowerCase().includes(keyword)
    || measurement.columns.some((column) =>
      column.name.toLowerCase().includes(keyword)
      || column.role.toLowerCase().includes(keyword)
      || column.dataType.toLowerCase().includes(keyword));
}

function tableMatchesFilter(table: TableInfo, keyword: string): boolean {
  return table.name.toLowerCase().includes(keyword)
    || table.columns.some((column) =>
      column.name.toLowerCase().includes(keyword)
      || column.dataType.toLowerCase().includes(keyword))
    || table.indexes.some((index) => index.name.toLowerCase().includes(keyword));
}

function documentCollectionMatchesFilter(collection: DocumentCollectionInfo, keyword: string): boolean {
  return collection.name.toLowerCase().includes(keyword)
    || collection.jsonIndexes.some((index) =>
      index.name.toLowerCase().includes(keyword) || index.path.toLowerCase().includes(keyword))
    || collection.fullTextIndexes.some((index) =>
      index.name.toLowerCase().includes(keyword)
      || index.fields.some((field) => field.toLowerCase().includes(keyword)));
}

function indexMatchesFilter(index: IndexLifecycleInfo, keyword: string): boolean {
  return index.id.toLowerCase().includes(keyword)
    || index.model.toLowerCase().includes(keyword)
    || index.owner.toLowerCase().includes(keyword)
    || index.name.toLowerCase().includes(keyword)
    || index.kind.toLowerCase().includes(keyword)
    || index.columns.some((column) => column.toLowerCase().includes(keyword));
}

function databaseMeta(
  loaded: boolean,
  loading: boolean,
  error: string,
  measurementCount: number,
  tableCount: number,
  documentCount: number,
  indexCount: number,
): string {
  if (loading) return 'loading schema...';
  if (error) return error;
  if (!loaded) return 'click to load schema';
  if (measurementCount + tableCount + documentCount === 0) return 'empty database';
  return `${measurementCount}M · ${tableCount}T · ${documentCount}D · ${indexCount}I`;
}

function databaseEmptyText(loaded: boolean, loading: boolean, error: string, keyword: string): string {
  if (loading) return 'Loading schema...';
  if (error) return error;
  if (!loaded) return keyword ? 'No matching measurements yet.' : 'Expand this database to load measurements.';
  return keyword ? 'No matching measurements.' : 'No measurements found.';
}

function stringifyValue(value: unknown): string {
  return formatSqlValue(value).toLowerCase();
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

function measurementMeta(measurement: MeasurementInfo): string {
  const tags = countColumns(measurement, 'TAG');
  const fields = countColumns(measurement, 'FIELD');
  return `${tags} TAG · ${fields} FIELD · ${measurement.columns.length} cols`;
}

function tableMeta(table: TableInfo): string {
  return `${table.columns.length} cols · pk ${table.primaryKey.join(', ')} · ${table.indexes.length} idx`;
}

function documentCollectionMeta(collection: DocumentCollectionInfo): string {
  return `${collection.jsonIndexes.length} json · ${collection.fullTextIndexes.length} fulltext`;
}

function indexMeta(index: IndexLifecycleInfo): string {
  return `${index.kind} · ${index.state}${index.rebuildable ? ' · rebuildable' : ''}`;
}

function backupMeta(status: BackupStatusInfo | null): string {
  if (!status) return 'backup status unavailable';
  const size = status.totalBytes >= 1024 * 1024
    ? `${(status.totalBytes / 1024 / 1024).toFixed(1)} MiB`
    : `${Math.max(status.totalBytes, 0)} B`;
  return `${status.segmentCount} seg · ${status.walFileCount} wal · ${size}`;
}

function openTable(table: TableInfo): void {
  setWorkbenchTool('sql');
  setSqlDraft(`DESCRIBE TABLE ${formatSqlIdentifier(table.name)}`);
}

function openDocumentCollection(collection: DocumentCollectionInfo): void {
  setWorkbenchTool('sql');
  setSqlDraft(`DESCRIBE DOCUMENT COLLECTION ${formatSqlIdentifier(collection.name)}`);
}

function selectIndex(db: string, index: IndexLifecycleInfo): void {
  selectDatabase(db);
  activeExplorerKey.value = index.id;
}

function showIndex(index: IndexLifecycleInfo): void {
  setWorkbenchTool('sql');
  if (index.model === 'table') {
    setSqlDraft(`SHOW INDEXES ON ${formatSqlIdentifier(index.owner)}`);
    return;
  }
  if (index.kind === 'json_path') {
    setSqlDraft(`SHOW JSON INDEXES ON ${formatSqlIdentifier(index.owner)}`);
    return;
  }
  if (index.kind === 'fulltext') {
    setSqlDraft(`SHOW FULLTEXT INDEXES ON ${formatSqlIdentifier(index.owner)}`);
    return;
  }
  setSqlDraft(`DESCRIBE MEASUREMENT ${formatSqlIdentifier(index.owner)}`);
}

async function runHealthCheck(): Promise<void> {
  await runMaintenanceAction(
    'health_check',
    () => ({ operation: 'health_check' }),
    (result) => `${result.message} ${result.checks.length} checks`,
  );
}

async function rebuildSelectedIndex(): Promise<void> {
  const index = selectedIndex.value;
  if (!index) {
    message.error('请先在左侧选择一个索引。');
    return;
  }
  await runMaintenanceAction(
    `rebuild:${index.id}`,
    () => ({
      operation: 'rebuild_index',
      targetModel: index.kind === 'fulltext' ? 'document_fulltext' : index.model,
      targetOwner: index.owner,
      targetName: index.name,
    }),
    (result) => result.index?.planned ? '索引重建计划已返回。' : result.message,
  );
}

async function verifyBackup(): Promise<void> {
  const path = maintenanceBackupDirectory.value.trim();
  if (!path) {
    message.error('请填写备份目录。');
    return;
  }
  await runMaintenanceAction(
    'backup_verify',
    () => ({ operation: 'backup_verify', backupDirectory: path }),
    (result) => result.backupVerification?.isValid
      ? `备份校验通过，检查 ${result.backupVerification.checkedFiles} 个文件。`
      : result.message,
  );
}

async function restoreDryRun(): Promise<void> {
  const backupDirectory = maintenanceBackupDirectory.value.trim();
  const restoreTargetDirectory = maintenanceRestoreTargetDirectory.value.trim();
  if (!backupDirectory || !restoreTargetDirectory) {
    message.error('请填写备份目录和恢复目标目录。');
    return;
  }
  await runMaintenanceAction(
    'restore_dry_run',
    () => ({
      operation: 'restore_dry_run',
      backupDirectory,
      restoreTargetDirectory,
      overwrite: true,
    }),
    (result) => result.restoreDryRun?.isValid
      ? `恢复 dry-run 通过，${result.restoreDryRun.fileCount} 个文件。`
      : result.message,
  );
}

async function runMaintenanceAction(
  busyKey: string,
  requestFactory: () => Parameters<typeof runMaintenance>[2],
  successText: (result: MaintenanceResponse) => string,
): Promise<void> {
  const db = targetDb.value;
  if (!db || db === CONTROL_PLANE_KEY) {
    message.error('请先选择业务数据库。');
    return;
  }

  maintenanceBusy.value = busyKey;
  try {
    const result = await runMaintenance(auth.api, db, requestFactory());
    maintenanceLastResult.value = result;
    if (result.success) {
      message.success(successText(result));
    } else {
      message.warning(result.message);
    }
    await loadSchema(db, true);
  } catch (error) {
    const errorMessage = error instanceof Error ? error.message : '维护操作失败';
    maintenanceLastResult.value = null;
    message.error(errorMessage);
  } finally {
    maintenanceBusy.value = '';
  }
}

function openCreateDatabaseDialog(): void {
  if (!auth.isSuperuser || databaseActionBusy.value) return;
  newDatabaseName.value = '';
  showCreateDatabaseDialog.value = true;
}

function closeCreateDatabaseDialog(): void {
  if (databaseActionBusy.value) return;
  showCreateDatabaseDialog.value = false;
  newDatabaseName.value = '';
}

function toggleGroup(key: string): void {
  openGroups.value[key] = !openGroups.value[key];
}

function toggleDatabaseExpansion(db: string): void {
  expandedDatabases.value = {
    ...expandedDatabases.value,
    [db]: !expandedDatabases.value[db],
  };
  if (expandedDatabases.value[db] && !hasCachedSchema(db) && db !== CONTROL_PLANE_KEY) {
    void loadSchema(db, false, db === targetDb.value);
  }
}

function selectDatabase(db: string): void {
  if (db === CONTROL_PLANE_KEY && !auth.isSuperuser) return;
  targetDb.value = db;
  if (db !== CONTROL_PLANE_KEY) {
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [db]: true,
    };
    void loadSchema(db);
  } else {
    schema.value = [];
    activeExplorerKey.value = '';
  }
}

function selectMeasurement(db: string, measurement: MeasurementInfo): void {
  selectDatabase(db);
  activeExplorerKey.value = measurement.name;
}

function selectTable(db: string, table: TableInfo): void {
  selectDatabase(db);
  activeExplorerKey.value = `table:${table.name}`;
}

function selectDocumentCollection(db: string, collection: DocumentCollectionInfo): void {
  selectDatabase(db);
  activeExplorerKey.value = `document:${collection.name}`;
}

function selectBackupStatus(db: string): void {
  selectDatabase(db);
  activeExplorerKey.value = 'backup-status';
}

function openMeasurement(measurement: MeasurementInfo): void {
  setWorkbenchTool('sql');
  setSqlDraft(buildSelectDraft(measurement, false));
}

async function refreshWorkbench(): Promise<void> {
  await reloadDbs();
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    await loadSchema(targetDb.value, true);
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
    syncDatabaseState(result.databases);
    normalizeTarget();
  } finally {
    loadingDbs.value = false;
  }
}

function syncDatabaseState(currentDatabases: string[]): void {
  const currentSet = new Set(currentDatabases);
  schemaByDb.value = Object.fromEntries(
    Object.entries(schemaByDb.value).filter(([name]) => currentSet.has(name)),
  );
  schemaLoadingByDb.value = Object.fromEntries(
    Object.entries(schemaLoadingByDb.value).filter(([name]) => currentSet.has(name)),
  );
  schemaErrorByDb.value = Object.fromEntries(
    Object.entries(schemaErrorByDb.value).filter(([name]) => currentSet.has(name)),
  );
  expandedDatabases.value = Object.fromEntries(
    Object.entries(expandedDatabases.value).filter(([name]) => currentSet.has(name)),
  );
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

async function loadSchema(db: string, force = false, syncActive = true): Promise<void> {
  if (!db || db === CONTROL_PLANE_KEY) {
    if (syncActive && targetDb.value === db) {
      schema.value = [];
      activeExplorerKey.value = '';
    }
    return;
  }

  if (hasCachedSchema(db) && !force) {
    if (syncActive && targetDb.value === db) {
      const dbSchema = schemaByDb.value[db];
      schema.value = dbSchema?.measurements ?? [];
      if (dbSchema) {
        activeExplorerKey.value = normalizeActiveExplorerKey(dbSchema);
      }
    }
    return;
  }

  schemaLoadingByDb.value = {
    ...schemaLoadingByDb.value,
    [db]: true,
  };
  schemaErrorByDb.value = {
    ...schemaErrorByDb.value,
    [db]: '',
  };
  loadingSchema.value = true;
  try {
    const resp = await fetchSchema(auth.api, db);
    const measurements = resp.measurements ?? [];
    schemaByDb.value = {
      ...schemaByDb.value,
      [db]: {
        measurements,
        tables: resp.tables ?? [],
        documentCollections: resp.documentCollections ?? [],
        indexes: resp.indexes ?? [],
        backupStatus: resp.backupStatus ?? null,
      },
    };
    if (syncActive && targetDb.value === db) {
      schema.value = measurements;
      activeExplorerKey.value = normalizeActiveExplorerKey(schemaByDb.value[db]);
    }
  } catch (error) {
    const errorMessage = error instanceof Error ? error.message : '加载 Schema 失败';
    schemaErrorByDb.value = {
      ...schemaErrorByDb.value,
      [db]: errorMessage,
    };
    if (syncActive && targetDb.value === db) {
      schema.value = [];
      activeExplorerKey.value = '';
    }
  } finally {
    schemaLoadingByDb.value = {
      ...schemaLoadingByDb.value,
      [db]: false,
    };
    loadingSchema.value = false;
  }
}

async function createDatabase(): Promise<void> {
  const name = newDatabaseName.value.trim();
  if (!isValidIdentifier(name)) {
    message.error('数据库名必须以字母开头，仅包含字母数字下划线。');
    return;
  }
  if (!auth.isSuperuser) {
    message.error('当前账号没有创建数据库权限。');
    return;
  }

  databaseActionBusy.value = true;
  try {
    const rs = await execControlPlaneSql(auth.api, `CREATE DATABASE ${name}`);
    if (rs.error) {
      message.error(rs.error.message);
      return;
    }
    message.success(`已创建数据库 ${name}`);
    newDatabaseName.value = '';
    showCreateDatabaseDialog.value = false;
    await reloadDbs();
    selectDatabase(name);
  } finally {
    databaseActionBusy.value = false;
  }
}

async function dropActiveDatabase(): Promise<void> {
  if (!canDropDatabase.value) return;
  const db = targetDb.value;
  databaseActionBusy.value = true;
  try {
    const rs = await execControlPlaneSql(auth.api, `DROP DATABASE ${db}`);
    if (rs.error) {
      message.error(rs.error.message);
      return;
    }
    message.success(`已删除数据库 ${db}`);
    schemaByDb.value = Object.fromEntries(
      Object.entries(schemaByDb.value).filter(([name]) => name !== db),
    );
    await reloadDbs();
    normalizeTarget();
    if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
      await loadSchema(targetDb.value, true);
    }
  } finally {
    databaseActionBusy.value = false;
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
  const formatted = formatSqlDocument(sql.value);
  if (!formatted.trim()) return;
  setSqlDraft(formatted);
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
    const text = formatSqlValue(value);
    const normalized = text.replace(/\r?\n/g, ' ');
    return /[",\n]/.test(normalized) ? `"${normalized.replace(/"/g, '""')}"` : normalized;
  };

  const lines = [
    columns.map(escape).join(','),
    ...rows.map((row) => columns.map((column) => escape(row[column])).join(',')),
  ];
  return `${lines.join('\n')}\n`;
}

watch(targetDb, (db) => {
  if (db && db !== CONTROL_PLANE_KEY) {
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [db]: true,
    };
  }
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
    await loadSchema(targetDb.value, true);
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

.create-database-dialog {
  width: min(440px, calc(100vw - 32px));
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

.schema-group__items--children {
  gap: 8px;
}

.schema-child-block {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.schema-child-block__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 2px 8px 1px;
  color: var(--sndb-ink-soft);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
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

.maintenance-panel {
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.maintenance-panel__head,
.maintenance-panel__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.maintenance-panel__head {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.maintenance-panel__actions > :deep(.n-button) {
  flex: 1 1 0;
  min-width: 0;
}

.maintenance-panel__result {
  margin: 0;
  color: var(--sndb-ink-soft);
  font-size: 11px;
  line-height: 1.35;
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

.result-grid {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.result-grid :deep(.n-empty) {
  margin: 24px;
}

.result-alert {
  margin: 12px;
}

.result-panel {
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

  .workbench-context__badges {
    justify-content: flex-start;
  }
}
</style>
