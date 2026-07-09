<template>
  <div class="workbench-page">
    <SqlWorkbenchHeader
      :connection-label="connectionLabel"
      :connection-name="connections.activeProfile.name"
      :connection-options="connectionOptions"
      :active-tool="activeWorkbenchTool"
      :access-badges="accessBadges"
      @connection-select="onConnectionSelect"
      @open-connection="openConnectionDialog"
      @set-tool="setWorkbenchTool"
    />

    <n-dropdown
      placement="bottom-start"
      trigger="manual"
      :x="contextMenu.x"
      :y="contextMenu.y"
      :show="contextMenu.show"
      :options="contextMenuOptions"
      @clickoutside="hideExplorerContextMenu"
      @select="onExplorerContextSelect"
    />

    <CreateDatabaseDialog
      v-model:show="showCreateDatabaseDialog"
      v-model:name="newDatabaseName"
      :busy="databaseActionBusy"
      :can-create="canCreateDatabase"
      @cancel="closeCreateDatabaseDialog"
      @create="createDatabase"
    />

    <RemoteConnectionDialog
      v-model:show="showConnectionDialog"
      v-model:name="connectionForm.name"
      v-model:base-url="connectionForm.baseUrl"
      v-model:default-database="connectionForm.defaultDatabase"
      :can-save="canSaveConnection"
      @save="saveConnection"
    />

    <section class="workbench-frame">
      <ManagementExplorerSidebar
        v-model:schema-filter="schemaFilter"
        v-model:backup-directory="maintenanceBackupDirectory"
        v-model:restore-target-directory="maintenanceRestoreTargetDirectory"
        :is-superuser="auth.isSuperuser"
        :target-db="targetDb"
        :database-tree="databaseTree"
        :databases-length="databases.length"
        :system-tree-node="systemTreeNode"
        :open-groups="openGroups"
        :expanded-databases="expandedDatabases"
        :active-explorer-key="activeExplorerKey"
        :loading-schema="loadingSchema"
        :loading-dbs="loadingDbs"
        :database-action-busy="databaseActionBusy"
        :can-drop-database="canDropDatabase"
        :maintenance-busy="maintenanceBusy"
        :maintenance-status="maintenanceStatus"
        :selected-index="selectedIndex"
        :explorer-groups="explorerGroups"
        @open-create-database="openCreateDatabaseDialog"
        @drop-database="dropActiveDatabase"
        @refresh="refreshWorkbench"
        @toggle-group="toggleGroup"
        @select-database="selectDatabase"
        @toggle-database="toggleDatabaseExpansion"
        @select-item="selectExplorerItem"
        @open-item="openExplorerItem"
        @context-menu="openExplorerContextMenu"
        @health-check="runHealthCheck"
        @rebuild-index="rebuildSelectedIndex"
        @verify-backup="verifyBackup"
        @restore-dry-run="restoreDryRun"
      />

      <SqlQueryWorkspace
        v-if="activeWorkbenchTool === 'sql'"
        v-model:active-tab-id="activeTabId"
        v-model:sql="sql"
        :tabs="sqlConsole.tabs"
        :active-tab="activeTab"
        :target-db="targetDb"
        :current-schema="currentSchema"
        :running="running"
        :preview-plan="previewPlan"
        :preview-is-stale="previewIsStale"
        :quick-sql-options="quickSqlOptions"
        :result-summary="resultSummary"
        :error-msg="errorMsg"
        :ran-once="ranOnce"
        :latest-result-set="latestResultSet"
        :latest-result-sql="latestResultItem?.sql ?? ''"
        :file-name="sqlDraftTitle()"
        @create-tab="createTab"
        @close-tab="closeTab"
        @run="run"
        @explain="explainSql"
        @format="formatSql"
        @quick-sql-select="onQuickSqlSelect"
        @cancel-preview="cancelPreview"
        @confirm-preview="confirmPreview"
        @clear-error="clearActiveError"
        @history-select="openHistoryEntry"
      />

      <RelationalTableWorkbench
        v-else-if="activeWorkbenchTool === 'table'"
        :target-db="targetDb"
        :table="selectedTable"
        :tables="currentSchemaResponse?.tables ?? []"
        :loading="loadingSchema"
        @open-sql="openRelationSql"
        @refresh-schema="loadSchema(targetDb, true)"
      />

      <KvKeyspaceWorkbench
        v-else-if="activeWorkbenchTool === 'kv'"
        :target-db="targetDb"
        :keyspace="selectedKvKeyspace"
        :keyspaces="currentKvKeyspaces"
        :loading="loadingSchema"
        @select-keyspace="selectKvKeyspace"
        @refresh-schema="loadSchema(targetDb, true)"
      />

      <SonnetMqWorkbench
        v-else-if="activeWorkbenchTool === 'mq'"
        :target-db="targetDb"
        :topic="selectedMqTopic"
        :topics="currentMqTopics"
        :loading="loadingSchema"
        @select-topic="selectMqTopic"
        @refresh-schema="loadSchema(targetDb, true)"
      />

      <VectorSearchWorkbench
        v-else-if="activeWorkbenchTool === 'vector'"
        :target-db="targetDb"
        :index="selectedVectorIndex"
        :indexes="currentVectorIndexes"
        :loading="loadingSchema"
        @select-index="selectVectorIndex"
        @refresh-schema="loadSchema(targetDb, true)"
      />

      <FullTextSearchWorkbench
        v-else-if="activeWorkbenchTool === 'fulltext'"
        :target-db="targetDb"
        :index="selectedFullTextIndex"
        :indexes="currentFullTextIndexes"
        :loading="loadingSchema"
        @select-index="selectFullTextIndex"
        @refresh-schema="loadSchema(targetDb, true)"
      />

      <main v-else class="query-workspace">
        <TrajectoryMap
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
import { computed, onMounted, watch } from 'vue';
import { NDropdown, useMessage } from 'naive-ui';
import CreateDatabaseDialog from '@/components/CreateDatabaseDialog.vue';
import FullTextSearchWorkbench from '@/components/FullTextSearchWorkbench.vue';
import KvKeyspaceWorkbench from '@/components/KvKeyspaceWorkbench.vue';
import ManagementExplorerSidebar from '@/components/ManagementExplorerSidebar.vue';
import RelationalTableWorkbench from '@/components/RelationalTableWorkbench.vue';
import RemoteConnectionDialog from '@/components/RemoteConnectionDialog.vue';
import SonnetMqWorkbench from '@/components/SonnetMqWorkbench.vue';
import SqlQueryWorkspace from '@/components/SqlQueryWorkspace.vue';
import SqlWorkbenchHeader from '@/components/SqlWorkbenchHeader.vue';
import VectorSearchWorkbench from '@/components/VectorSearchWorkbench.vue';
import TrajectoryMap from '@/views/TrajectoryMap.vue';
import type { FullTextIndexStat, VectorIndexStat } from '@/api/management';
import { useSqlExecution } from '@/composables/useSqlExecution';
import { useSqlExplorer } from '@/composables/useSqlExplorer';
import { useSqlExplorerRouting } from '@/composables/useSqlExplorerRouting';
import { useSqlMaintenance } from '@/composables/useSqlMaintenance';
import { useSqlWorkbenchChrome } from '@/composables/useSqlWorkbenchChrome';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  CONTROL_PLANE_KEY,
  useSqlConsoleStore,
} from '@/stores/sqlConsole';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';

const auth = useAuthStore();
const connections = useConnectionsStore();
const sqlConsole = useSqlConsoleStore();
const workbenchHistory = useWorkbenchHistoryStore();
const message = useMessage();

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
  set: (db: string) => {
    sqlConsole.patchActiveTab({ db });
    connections.setActiveDatabase(db);
  },
});

const sql = computed({
  get: () => activeTab.value?.sql ?? '',
  set: (value: string) => {
    sqlConsole.patchActiveTab({ sql: value });
    if (previewPlan.value && previewPlan.value.tabId === activeTab.value?.id) {
      previewPlan.value = null;
    }
  },
});

const {
  showConnectionDialog,
  connectionForm,
  activeWorkbenchTool,
  connectionLabel,
  accessBadges,
  connectionOptions,
  canSaveConnection,
  setWorkbenchTool,
  openConnectionDialog,
  saveConnection,
  onConnectionSelect,
} = useSqlWorkbenchChrome({
  auth,
  connections,
  targetDb,
});

const {
  databases,
  managementByDb,
  schemaFilter,
  newDatabaseName,
  showCreateDatabaseDialog,
  databaseActionBusy,
  expandedDatabases,
  loadingDbs,
  loadingSchema,
  activeExplorerKey,
  openGroups,
  currentSchemaResponse,
  currentSchema,
  databaseTree,
  systemTreeNode,
  canCreateDatabase,
  canDropDatabase,
  trajectoryInitialDb,
  trajectoryInitialMeasurement,
  selectedMeasurement,
  selectedTable,
  selectedIndex,
  explorerGroups,
  openCreateDatabaseDialog,
  closeCreateDatabaseDialog,
  toggleGroup,
  toggleDatabaseExpansion,
  selectDatabase,
  refreshWorkbench,
  reloadDbs,
  loadSchema,
  createDatabase,
  dropActiveDatabase,
  resetExplorerCache,
} = useSqlExplorer({
  auth,
  sqlConsole,
  targetDb,
  activeTab,
  message,
});

const currentKvKeyspaces = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.kvKeyspaces ?? []
    : []);

const selectedKvKeyspace = computed(() => {
  const active = activeExplorerKey.value.startsWith('kv:')
    ? activeExplorerKey.value.slice('kv:'.length)
    : '';
  if (active && currentKvKeyspaces.value.includes(active)) return active;
  return currentKvKeyspaces.value[0] ?? '';
});

const currentMqTopics = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.mqTopics ?? []
    : []);

const selectedMqTopic = computed(() => {
  const active = activeExplorerKey.value.startsWith('mq:')
    ? activeExplorerKey.value.slice('mq:'.length)
    : '';
  if (active && currentMqTopics.value.some((topic) => topic.topic === active)) return active;
  return currentMqTopics.value[0]?.topic ?? active;
});

const currentVectorIndexes = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.vectorIndexes ?? []
    : []);

const selectedVectorIndex = computed(() => {
  const active = activeExplorerKey.value.startsWith('vector:')
    ? activeExplorerKey.value
    : '';
  if (active) {
    const selected = currentVectorIndexes.value.find((index) => vectorIndexKey(index) === active);
    if (selected) return selected;
  }
  return currentVectorIndexes.value[0] ?? null;
});

const currentFullTextIndexes = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.fullTextIndexes ?? []
    : []);

const selectedFullTextIndex = computed(() => {
  const active = activeExplorerKey.value.startsWith('fulltext:')
    ? activeExplorerKey.value
    : '';
  if (active) {
    const selected = currentFullTextIndexes.value.find((index) => fullTextIndexKey(index) === active);
    if (selected) return selected;
  }
  return currentFullTextIndexes.value[0] ?? null;
});

const {
  maintenanceBackupDirectory,
  maintenanceRestoreTargetDirectory,
  maintenanceBusy,
  maintenanceStatus,
  runHealthCheck,
  rebuildSelectedIndex,
  verifyBackup,
  restoreDryRun,
} = useSqlMaintenance({
  auth,
  connections,
  workbenchHistory,
  targetDb,
  selectedIndex,
  message,
  loadSchema,
});

const {
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
} = useSqlExecution({
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
});

const {
  contextMenu,
  contextMenuOptions,
  openExplorerContextMenu,
  hideExplorerContextMenu,
  onExplorerContextSelect,
  selectExplorerItem,
  openExplorerItem,
} = useSqlExplorerRouting({
  activeExplorerKey,
  message,
  selectDatabase,
  setWorkbenchTool,
  setSqlDraft,
  loadSchema,
  runHealthCheck,
});

function openRelationSql(sqlText: string): void {
  setWorkbenchTool('sql');
  setSqlDraft(sqlText);
}

function selectKvKeyspace(keyspace: string): void {
  if (!keyspace) return;
  activeExplorerKey.value = `kv:${keyspace}`;
  setWorkbenchTool('kv');
}

function selectMqTopic(topic: string): void {
  if (!topic) return;
  activeExplorerKey.value = `mq:${topic}`;
  setWorkbenchTool('mq');
}

function selectVectorIndex(index: VectorIndexStat): void {
  activeExplorerKey.value = vectorIndexKey(index);
  setWorkbenchTool('vector');
}

function vectorIndexKey(index: VectorIndexStat): string {
  return `vector:${index.measurement}:${index.column}`;
}

function selectFullTextIndex(index: FullTextIndexStat): void {
  activeExplorerKey.value = fullTextIndexKey(index);
  setWorkbenchTool('fulltext');
}

function fullTextIndexKey(index: FullTextIndexStat): string {
  return `fulltext:${index.collection}:${index.name}`;
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
  }
}, { immediate: false });

watch(() => connections.activeProfileId, async () => {
  auth.setApiBaseUrl(connections.activeBaseUrl);
  resetExplorerCache();
  if (connections.activeDatabase) {
    targetDb.value = connections.activeDatabase;
  }
  await reloadDbs();
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    await loadSchema(targetDb.value, true);
  }
});

watch(activeTabId, () => {
  cancelPreview();
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
  gap: 10px;
  height: calc(100vh - 96px);
  min-height: 680px;
  overflow: hidden;
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

.query-workspace {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

@media (max-width: 1280px) {
  .workbench-frame {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 840px) {
  .workbench-page {
    height: auto;
    min-height: 0;
  }
}
</style>
