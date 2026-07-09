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
import ManagementExplorerSidebar from '@/components/ManagementExplorerSidebar.vue';
import RemoteConnectionDialog from '@/components/RemoteConnectionDialog.vue';
import SqlQueryWorkspace from '@/components/SqlQueryWorkspace.vue';
import SqlWorkbenchHeader from '@/components/SqlWorkbenchHeader.vue';
import TrajectoryMap from '@/views/TrajectoryMap.vue';
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
  schemaFilter,
  newDatabaseName,
  showCreateDatabaseDialog,
  databaseActionBusy,
  expandedDatabases,
  loadingDbs,
  loadingSchema,
  activeExplorerKey,
  openGroups,
  currentSchema,
  databaseTree,
  systemTreeNode,
  canCreateDatabase,
  canDropDatabase,
  trajectoryInitialDb,
  trajectoryInitialMeasurement,
  selectedMeasurement,
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
