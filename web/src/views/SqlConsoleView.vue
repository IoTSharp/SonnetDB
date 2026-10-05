<template>
  <div class="workbench-page">
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

    <section class="workbench-frame" :class="{ 'is-explorer-collapsed': explorerCollapsed }">
      <ManagementExplorerSidebar
        v-model:schema-filter="schemaFilter"
        v-model:backup-directory="maintenanceBackupDirectory"
        v-model:restore-target-directory="maintenanceRestoreTargetDirectory"
        :collapsed="explorerCollapsed"
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
        :studio-bridge-available="studioBridgeAvailable"
        :explorer-groups="explorerGroups"
        @toggle-collapse="explorerCollapsed = !explorerCollapsed"
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
        @pick-backup-directory="pickMaintenanceBackupDirectory"
        @pick-restore-directory="pickMaintenanceRestoreDirectory"
      />

      <section class="workspace-shell">
        <StudioWorkspaceTabs
          :tabs="workspaceTabs"
          :active-tab-id="activeWorkspaceTabId"
          :connection-name="connections.activeProfile.name"
          :connection-options="connectionOptions"
          :studio-bridge-available="studioBridgeAvailable"
          :native-server-status="nativeServerStatus"
          :native-server-presentation="nativeServerPresentation"
          :studio-active-identity="studioActiveIdentity"
          :native-server-busy="nativeServerBusy"
          :native-data-root="nativeDataRoot"
          :connection-health-busy="connectionHealthBusy"
          @select-tab="selectWorkspaceTab"
          @close-tab="closeWorkspaceTab"
          @create-tab="createWorkspaceTab"
          @connection-select="onConnectionSelect"
          @open-connection="openConnectionDialog"
          @refresh-native-server="refreshNativeServerStatus"
          @refresh-connection-health="refreshConnectionHealth"
          @start-native-server="startNativeServer"
          @stop-native-server="stopNativeServer"
          @choose-native-data-root="chooseNativeDataRoot"
          @open-embedded-database="openEmbeddedDatabase"
          @update:native-data-root="setNativeDataRoot"
          @show-result="toggleResultDrawer"
          @show-history="globalHistoryVisible = true"
          @show-diagnostics="queryDiagnosticsVisible = true"
        />

        <SqlQueryWorkspace
        v-if="activeWorkbenchTool === 'sql'"
        v-model:active-tab-id="activeTabId"
        v-model:sql="sql"
        :tabs="sqlConsole.tabs"
        :closed-tabs="sqlConsole.closedTabs"
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
        @reopen-closed-tab="reopenClosedTab"
        @discard-closed-tab="discardClosedTab"
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

        <MeasurementWorkbench
        v-else-if="activeWorkbenchTool === 'measurement'"
        :target-db="targetDb"
        :measurement="selectedMeasurement"
        :measurements="currentSchema"
        :tables="currentSchemaResponse?.tables ?? []"
        :loading="loadingSchema"
        @open-sql="openRelationSql"
        @refresh-schema="loadSchema(targetDb, true)"
        />

        <DocumentCollectionWorkbench
        v-else-if="activeWorkbenchTool === 'document'"
        :target-db="targetDb"
        :collection="selectedDocumentCollection"
        :collections="currentDocumentCollections"
        :loading="loadingSchema"
        @select-collection="selectDocumentCollection"
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
        :measurement="selectedVectorMeasurement"
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

        <ObjectBucketWorkbench
        v-else-if="activeWorkbenchTool === 'bucket'"
        :target-db="targetDb"
        :bucket="selectedObjectBucket"
        :buckets="currentObjectBuckets"
        :loading="loadingSchema"
        @select-bucket="selectObjectBucket"
        @refresh-schema="loadSchema(targetDb, true)"
        />

        <GraphWorkbench
        v-else-if="activeWorkbenchTool === 'graph'"
        :target-db="targetDb"
        :graph="selectedGraph"
        :graphs="currentGraphs"
        :loading="loadingSchema"
        @select-graph="selectGraph"
        @refresh-graphs="loadSchema(targetDb, true)"
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
    </section>

    <WorkbenchHistoryDrawer
      v-model:show="globalHistoryVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />

    <SlowQueryDrawer
      v-model:show="queryDiagnosticsVisible"
      :active-database="targetDb"
      :databases="databases"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { NDropdown, useMessage } from 'naive-ui';
import { useRoute } from 'vue-router';
import AsyncWorkbenchLoading from '@/components/AsyncWorkbenchLoading.vue';
import CreateDatabaseDialog from '@/components/CreateDatabaseDialog.vue';
import ManagementExplorerSidebar from '@/components/ManagementExplorerSidebar.vue';
import RemoteConnectionDialog from '@/components/RemoteConnectionDialog.vue';
import SlowQueryDrawer from '@/components/SlowQueryDrawer.vue';
import SqlQueryWorkspace from '@/components/SqlQueryWorkspace.vue';
import StudioWorkspaceTabs, { type StudioWorkspaceTab } from '@/components/StudioWorkspaceTabs.vue';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import type { FullTextIndexStat, VectorIndexStat } from '@/api/management';
import type { DocumentCollectionInfo } from '@/api/schema';
import {
  currentStudioNativeBridge,
  subscribeStudioDesktopActions,
  type StudioDesktopActionMessage,
} from '@/api/studioNativeBridge';
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
import {
  createGraphResourceDescriptor,
  createMqResourceDescriptor,
  createResourceDescriptor,
  type ResourceDescriptor,
} from '@/management-core/resourceDescriptor';
import { explorerKeyFromRoute } from '@/utils/managementExplorer';
import type { WorkbenchTool } from '@/utils/sqlWorkbench';

const asyncWorkbenchOptions = { loadingComponent: AsyncWorkbenchLoading, delay: 120, suspensible: false };
const DocumentCollectionWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/DocumentCollectionWorkbench.vue'),
});
const FullTextSearchWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/FullTextSearchWorkbench.vue'),
});
const GraphWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/GraphWorkbench.vue'),
});
const KvKeyspaceWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/KvKeyspaceWorkbench.vue'),
});
const MeasurementWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/MeasurementWorkbench.vue'),
});
const ObjectBucketWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/ObjectBucketWorkbench.vue'),
});
const RelationalTableWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/RelationalTableWorkbench.vue'),
});
const SonnetMqWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/SonnetMqWorkbench.vue'),
});
const VectorSearchWorkbench = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/components/VectorSearchWorkbench.vue'),
});
const TrajectoryMap = defineAsyncComponent({
  ...asyncWorkbenchOptions,
  loader: () => import('@/views/TrajectoryMap.vue'),
});

const auth = useAuthStore();
const connections = useConnectionsStore();
const sqlConsole = useSqlConsoleStore();
const workbenchHistory = useWorkbenchHistoryStore();
const route = useRoute();
const message = useMessage();
const explorerCollapsed = ref(false);
const globalHistoryVisible = ref(false);
const queryDiagnosticsVisible = ref(false);
const objectWorkspaceTabs = ref<StudioWorkspaceTab[]>([]);

function toggleResultDrawer(): void {
  window.dispatchEvent(new CustomEvent('sndb:toggle-result'));
}

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
  studioBridgeAvailable,
  nativeServerStatus,
  nativeServerPresentation,
  studioActiveIdentity,
  nativeServerBusy,
  nativeDataRoot,
  connectionHealthBusy,
  activeWorkbenchTool,
  connectionOptions,
  canSaveConnection,
  setWorkbenchTool,
  openConnectionDialog,
  saveConnection,
  onConnectionSelect,
  refreshNativeServerStatus,
  refreshConnectionHealth,
  startNativeServer,
  chooseNativeDataRoot,
  openNativeEmbeddedDatabase,
  setNativeDataRoot,
  stopNativeServer,
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

async function openEmbeddedDatabase(): Promise<void> {
  try {
    if (await openNativeEmbeddedDatabase()) await refreshWorkbench();
  } catch (error) {
    message.error(error instanceof Error ? error.message : '打开嵌入式数据库失败');
  }
}

const currentKvKeyspaces = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.kvKeyspaces ?? []
    : []);

const currentDocumentCollections = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? currentSchemaResponse.value?.documentCollections ?? []
    : []);

const selectedDocumentCollection = computed(() => {
  const active = activeExplorerKey.value.startsWith('document:')
    ? activeExplorerKey.value.slice('document:'.length)
    : '';
  if (active) {
    const selected = currentDocumentCollections.value.find((collection) => collection.name === active);
    if (selected) return selected;
  }
  return currentDocumentCollections.value[0] ?? null;
});

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

const selectedVectorMeasurement = computed(() => {
  const measurementName = selectedVectorIndex.value?.measurement;
  return currentSchemaResponse.value?.measurements.find((item) => item.name === measurementName) ?? null;
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

const currentObjectBuckets = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.buckets ?? []
    : []);

const selectedObjectBucket = computed(() => {
  const active = activeExplorerKey.value.startsWith('bucket:')
    ? activeExplorerKey.value.slice('bucket:'.length)
    : '';
  if (active && currentObjectBuckets.value.some((bucket) => bucket.name === active)) return active;
  return currentObjectBuckets.value[0]?.name ?? active;
});

const currentGraphs = computed(() =>
  targetDb.value && targetDb.value !== CONTROL_PLANE_KEY
    ? managementByDb.value[targetDb.value]?.graphs ?? []
    : []);

const selectedGraph = computed(() => {
  const active = activeExplorerKey.value.startsWith('graph:')
    ? activeExplorerKey.value.slice('graph:'.length)
    : '';
  if (active && currentGraphs.value.some((graph) => graph.name === active)) return active;
  return currentGraphs.value[0]?.name ?? active;
});

const activeObjectIdentity = computed(() => {
  if (activeWorkbenchTool.value === 'trajectory') {
    return { label: '轨迹分析', key: activeExplorerKey.value || 'trajectory', resource: null };
  }
  // Object descriptors are database-scoped. During initial connection setup
  // (or on the control plane) keep the empty workbench state without creating
  // a synthetic resource identity.
  if (!targetDb.value || targetDb.value === CONTROL_PLANE_KEY) return null;

  switch (activeWorkbenchTool.value) {
    case 'measurement':
      if (!selectedMeasurement.value) return null;
      return {
        label: selectedMeasurement.value.name,
        key: selectedMeasurement.value.name,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'measurement',
          name: selectedMeasurement.value.name,
          key: selectedMeasurement.value.name,
          legacyKey: selectedMeasurement.value.name,
        }),
      };
    case 'table':
      if (!selectedTable.value) return null;
      return {
        label: selectedTable.value.name,
        key: `table:${selectedTable.value.name}`,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'table',
          name: selectedTable.value.name,
          key: `table:${selectedTable.value.name}`,
          legacyKey: `table:${selectedTable.value.name}`,
        }),
      };
    case 'document':
      if (!selectedDocumentCollection.value) return null;
      return {
        label: selectedDocumentCollection.value.name,
        key: `document:${selectedDocumentCollection.value.name}`,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'document',
          name: selectedDocumentCollection.value.name,
          key: `document:${selectedDocumentCollection.value.name}`,
          legacyKey: `document:${selectedDocumentCollection.value.name}`,
        }),
      };
    case 'kv':
      if (!selectedKvKeyspace.value) return null;
      return {
        label: selectedKvKeyspace.value,
        key: `kv:${selectedKvKeyspace.value}`,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'kv',
          name: selectedKvKeyspace.value,
          key: `kv:${selectedKvKeyspace.value}`,
          legacyKey: `kv:${selectedKvKeyspace.value}`,
        }),
      };
    case 'mq':
      if (!selectedMqTopic.value) return null;
      return {
        label: selectedMqTopic.value,
        key: `mq:${selectedMqTopic.value}`,
        resource: createMqTabResourceDescriptor(targetDb.value, selectedMqTopic.value),
      };
    case 'vector': {
      const index = selectedVectorIndex.value;
      if (!index) return null;
      const key = vectorIndexKey(index);
      return {
        label: `${index.measurement}.${index.column}`,
        key,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'vector',
          name: `${index.measurement}.${index.column}`,
          key,
          legacyKey: key,
        }),
      };
    }
    case 'fulltext': {
      const index = selectedFullTextIndex.value;
      if (!index) return null;
      const key = fullTextIndexKey(index);
      return {
        label: `${index.collection}.${index.name}`,
        key,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'fulltext',
          name: `${index.collection}.${index.name}`,
          key,
          legacyKey: key,
        }),
      };
    }
    case 'bucket':
      if (!selectedObjectBucket.value) return null;
      return {
        label: selectedObjectBucket.value,
        key: `bucket:${selectedObjectBucket.value}`,
        resource: createResourceDescriptor({
          database: targetDb.value,
          model: 'bucket',
          name: selectedObjectBucket.value,
          key: `bucket:${selectedObjectBucket.value}`,
          legacyKey: `bucket:${selectedObjectBucket.value}`,
        }),
      };
    case 'graph':
      if (!selectedGraph.value) return null;
      return {
        label: selectedGraph.value,
        key: `graph:${selectedGraph.value}`,
        resource: createGraphResourceDescriptor(
          targetDb.value,
          selectedGraph.value,
          `graph:${selectedGraph.value}`,
        ),
      };
    default:
      return null;
  }
});

const workspaceTabs = computed<StudioWorkspaceTab[]>(() => [
  ...sqlConsole.tabs.map((tab) => ({
    id: `sql:${tab.id}`,
    label: tab.title,
    tool: 'sql' as WorkbenchTool,
    db: tab.db,
    objectKey: tab.id,
    resourceIdentity: undefined,
    closable: sqlConsole.tabs.length > 1,
  })),
  ...objectWorkspaceTabs.value,
]);

const activeWorkspaceTabId = computed(() => {
  if (activeWorkbenchTool.value === 'sql') return `sql:${activeTabId.value}`;
  const identity = activeObjectIdentity.value;
  if (!identity) return '';
  return objectTabId(activeWorkbenchTool.value, targetDb.value, identity.key);
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

async function pickMaintenanceBackupDirectory(): Promise<void> {
  const selected = await connections.selectStudioDirectory('选择备份目录', maintenanceBackupDirectory.value);
  if (selected) maintenanceBackupDirectory.value = selected;
}

async function pickMaintenanceRestoreDirectory(): Promise<void> {
  const selected = await connections.selectStudioDirectory('选择恢复演练目标目录', maintenanceRestoreTargetDirectory.value);
  if (selected) maintenanceRestoreTargetDirectory.value = selected;
}

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

function objectTabId(tool: WorkbenchTool, db: string, objectKey: string): string {
  return `object:${tool}:${db}:${objectKey}`;
}

function createMqTabResourceDescriptor(database: string, topic: string): ResourceDescriptor {
  return createMqResourceDescriptor(database, topic);
}

function selectWorkspaceTab(id: string): void {
  if (id.startsWith('sql:')) {
    sqlConsole.activateTab(id.slice('sql:'.length));
    setWorkbenchTool('sql');
    return;
  }

  const tab = objectWorkspaceTabs.value.find((item) => item.id === id);
  if (!tab) return;
  if (tab.db && targetDb.value !== tab.db) targetDb.value = tab.db;
  activeExplorerKey.value = tab.objectKey;
  setWorkbenchTool(tab.tool);
}

function reopenClosedTab(id: string): void {
  const tab = sqlConsole.reopenTab(id);
  if (!tab) return;
  setWorkbenchTool('sql');
  void loadSchema(tab.db);
}

function discardClosedTab(id: string): void {
  sqlConsole.discardClosedTab(id);
}

function closeWorkspaceTab(id: string): void {
  if (id.startsWith('sql:')) {
    closeTab(id.slice('sql:'.length));
    return;
  }

  objectWorkspaceTabs.value = objectWorkspaceTabs.value.filter((tab) => tab.id !== id);
  if (activeWorkspaceTabId.value === id) setWorkbenchTool('sql');
}

function createWorkspaceTab(): void {
  createTab();
  setWorkbenchTool('sql');
}

async function handleStudioDesktopAction(action: StudioDesktopActionMessage): Promise<void> {
  switch (action.id) {
    case 'query.new':
      createWorkspaceTab();
      return;
    case 'file.open':
      await openSqlFromDesktop();
      return;
    case 'file.save':
      await saveSqlFromDesktop();
      return;
    case 'view.results':
      toggleResultDrawer();
      return;
    case 'view.history':
      globalHistoryVisible.value = true;
      return;
    case 'server.start':
      await startNativeServer();
      return;
    case 'server.stop':
      await stopNativeServer();
      return;
    case 'server.health':
      await refreshNativeServerStatus();
      return;
    default:
      return;
  }
}

async function openSqlFromDesktop(): Promise<void> {
  const bridge = currentStudioNativeBridge();
  if (!bridge) return;
  try {
    const result = await bridge.openTextFile({
      title: '打开 SQL 文件',
      filters: [
        { name: 'SQL files', extensions: ['sql'] },
        { name: 'Text files', extensions: ['txt'] },
      ],
      maxBytes: 4 * 1024 * 1024,
    });
    if (result.error) throw new Error(result.error);
    if (result.canceled) return;

    sqlConsole.createTab({
      title: result.fileName || 'Imported SQL',
      sql: result.content || '',
      source: 'manual',
    });
    setWorkbenchTool('sql');
    message.success(`已打开 ${result.fileName || 'SQL 文件'}`);
  } catch (error) {
    message.error(error instanceof Error ? error.message : '打开 SQL 文件失败');
  }
}

async function saveSqlFromDesktop(): Promise<void> {
  const bridge = currentStudioNativeBridge();
  if (!bridge || !activeTab.value) return;
  try {
    const result = await bridge.saveTextFile({
      title: '保存 SQL 文件',
      suggestedName: sqlFileName(activeTab.value.title),
      content: activeTab.value.sql,
      contentType: 'application/sql; charset=utf-8',
      filters: [{ name: 'SQL files', extensions: ['sql'] }],
    });
    if (result.error) throw new Error(result.error);
    if (!result.canceled) message.success(`已保存 ${result.fileName || 'SQL 文件'}`);
  } catch (error) {
    message.error(error instanceof Error ? error.message : '保存 SQL 文件失败');
  }
}

function sqlFileName(title: string): string {
  const normalized = title.trim().replace(/[\\/:*?"<>|]+/gu, '-').replace(/\s+/gu, '-');
  const baseName = normalized || 'query';
  return baseName.toLowerCase().endsWith('.sql') ? baseName : `${baseName}.sql`;
}

function handleStudioShortcut(event: KeyboardEvent): void {
  if (!studioBridgeAvailable.value || (!event.ctrlKey && !event.metaKey) || event.altKey) return;
  const key = event.key.toLowerCase();
  let id: StudioDesktopActionMessage['id'] | null = null;
  if (key === 'n' && !event.shiftKey) id = 'query.new';
  if (key === 'o' && !event.shiftKey) id = 'file.open';
  if (key === 's' && !event.shiftKey) id = 'file.save';
  if (key === 'h' && !event.shiftKey) id = 'view.history';
  if (key === 'r' && event.shiftKey) id = 'view.results';
  if (!id) return;

  event.preventDefault();
  void handleStudioDesktopAction({ id });
}

function handleWorkbenchResize(): void {
  if (window.innerWidth < 1100)
    explorerCollapsed.value = true;
}

function openRelationSql(sqlText: string): void {
  setWorkbenchTool('sql');
  setSqlDraft(sqlText);
}

function selectDocumentCollection(collection: DocumentCollectionInfo): void {
  activeExplorerKey.value = `document:${collection.name}`;
  setWorkbenchTool('document');
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

function selectObjectBucket(bucket: string): void {
  if (!bucket) return;
  activeExplorerKey.value = `bucket:${bucket}`;
  setWorkbenchTool('bucket');
}

function selectGraph(graph: string): void {
  if (!graph) return;
  activeExplorerKey.value = `graph:${graph}`;
  setWorkbenchTool('graph');
}

// A legacy tool/model/node URL identifies the Explorer item by its display
// name. Apply that selection once after the active database has both schema
// and management metadata; route-only links still do not execute SQL.
const routeSelectionToken = ref('');
// A database-bearing link must wait until the database list has been loaded.
// This prevents the initial active/default database from resolving the node
// while the requested database is still unknown.
const routeDatabaseListReady = ref(false);
// Tracks the database query value already applied for this connection. A
// user changing the Explorer database must not be redirected by the same URL;
// only a new query value or a fresh database-list load can drive selection.
const routeDatabaseSelectionToken = ref('');

function routeDatabaseQuery(): string | undefined {
  const value = route.query.database;
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function applyRouteDatabaseSelection(): 'missing' | 'pending' | 'valid' | 'invalid' {
  const requested = routeDatabaseQuery();
  if (!requested) {
    routeDatabaseSelectionToken.value = '';
    return 'missing';
  }
  if (!routeDatabaseListReady.value) return 'pending';
  if (!databases.value.includes(requested)) return 'invalid';
  if (routeDatabaseSelectionToken.value !== requested) {
    routeDatabaseSelectionToken.value = requested;
    routeSelectionToken.value = '';
    if (targetDb.value !== requested) selectDatabase(requested);
  }
  return 'valid';
}

watch(
  [
    () => route.query.tool,
    () => route.query.model,
    () => route.query.node,
    () => route.query.database,
    targetDb,
    databases,
    routeDatabaseListReady,
    currentSchemaResponse,
    () => (targetDb.value ? managementByDb.value[targetDb.value] : undefined),
  ],
  ([toolValue, modelValue, nodeValue, _databaseValue, db, _databases, dbListReady, dbSchema, management]) => {
    const rawModel = typeof modelValue === 'string' ? modelValue : undefined;
    const tool = typeof toolValue === 'string' ? toolValue : undefined;
    const model = rawModel ?? (tool && [
      'measurement', 'table', 'document', 'kv', 'mq', 'vector', 'fulltext', 'bucket', 'graph', 'index', 'backup',
    ].includes(tool) ? tool : undefined);
    const node = typeof nodeValue === 'string' ? nodeValue : undefined;
    const requestedDatabase = routeDatabaseQuery();
    if (!model) {
      if (!requestedDatabase) {
        routeDatabaseSelectionToken.value = '';
      } else if (!dbListReady) {
        return;
      } else if (!_databases.includes(requestedDatabase)) {
        routeDatabaseSelectionToken.value = `invalid-db\u0000${requestedDatabase}`;
        routeSelectionToken.value = `invalid-db\u0000${requestedDatabase}`;
      } else if (routeDatabaseSelectionToken.value !== requestedDatabase) {
        routeDatabaseSelectionToken.value = requestedDatabase;
        if (db !== requestedDatabase) selectDatabase(requestedDatabase);
      }
      routeSelectionToken.value = '';
      return;
    }

    if (requestedDatabase && !dbListReady) return;
    if (requestedDatabase) {
      if (!_databases.includes(requestedDatabase)) {
        // An unknown database must not resolve its node against the active
        // database's first item. Keep the active/default database intact and
        // leave Explorer selection untouched until the URL is corrected.
        routeDatabaseSelectionToken.value = `invalid-db\u0000${requestedDatabase}`;
        routeSelectionToken.value = `invalid-db\u0000${requestedDatabase}`;
        return;
      }
      if (routeDatabaseSelectionToken.value !== requestedDatabase) {
        routeDatabaseSelectionToken.value = requestedDatabase;
        routeSelectionToken.value = '';
        if (db !== requestedDatabase) {
          selectDatabase(requestedDatabase);
          return;
        }
      } else if (db !== requestedDatabase) {
        // The URL database was already applied. A later targetDb change came
        // from the user, so preserve that choice instead of forcing the URL
        // database. Keep evaluating the model/node against the current db;
        // this lets a browser navigation that only changes model/node resolve
        // in the manually selected database and safely fall back locally.
        routeSelectionToken.value = '';
      }
    }

    if (!db || db === CONTROL_PLANE_KEY || !dbSchema || !management) return;

    const token = `${requestedDatabase ?? ''}\u0000${db}\u0000${model}\u0000${node ?? ''}`;
    if (routeSelectionToken.value === token) return;
    routeSelectionToken.value = token;
    activeExplorerKey.value = explorerKeyFromRoute(model, node, dbSchema, management);
  },
  { immediate: true },
);

watch([activeWorkbenchTool, activeObjectIdentity, targetDb], ([tool, identity, db]) => {
  if (tool === 'sql' || !identity) return;
  if (identity.key !== tool) {
    objectWorkspaceTabs.value = objectWorkspaceTabs.value.filter((tab) =>
      !(tab.tool === tool && tab.db === db && tab.objectKey === tool));
  }
  const id = objectTabId(tool, db, identity.key);
  const existing = objectWorkspaceTabs.value.findIndex((tab) => tab.id === id);
  const tab: StudioWorkspaceTab = {
    id,
    label: identity.label,
    tool,
    db,
    objectKey: identity.key,
    resourceIdentity: identity.resource ? {
      database: identity.resource.database,
      resource: identity.resource,
      legacyKey: identity.resource.legacyKey,
    } : undefined,
    closable: true,
  };
  if (existing >= 0) {
    objectWorkspaceTabs.value = objectWorkspaceTabs.value.map((item, index) => index === existing ? tab : item);
  } else {
    objectWorkspaceTabs.value = [...objectWorkspaceTabs.value, tab];
  }
}, { immediate: true });

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

watch([() => connections.activeProfileId, () => connections.activeBaseUrl], async (_profile, _previous, onCleanup) => {
  let cancelled = false;
  onCleanup(() => { cancelled = true; });
  auth.setApiBaseUrl(connections.activeBaseUrl);
  routeSelectionToken.value = '';
  routeDatabaseSelectionToken.value = '';
  routeDatabaseListReady.value = false;
  resetExplorerCache();
  if (connections.activeDatabase) {
    targetDb.value = connections.activeDatabase;
  }
  await reloadDbs();
  if (cancelled) return;
  routeDatabaseListReady.value = true;
  applyRouteDatabaseSelection();
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [targetDb.value]: true,
    };
    await loadSchema(targetDb.value, true);
    if (cancelled) return;
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [targetDb.value]: true,
    };
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

let unsubscribeDesktopActions: (() => void) | null = null;

function refreshSchemaOnFocus(): void {
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    void loadSchema(targetDb.value, true);
  }
}

onMounted(async () => {
  unsubscribeDesktopActions = subscribeStudioDesktopActions(handleStudioDesktopAction);
  window.addEventListener('keydown', handleStudioShortcut);
  window.addEventListener('resize', handleWorkbenchResize);
  window.addEventListener('focus', refreshSchemaOnFocus);
  handleWorkbenchResize();
  const bridgeReady = await connections.connectStudioBridge();
  if (bridgeReady) {
    if (connections.studioActiveIdentity) targetDb.value = connections.activeDatabase;
    auth.setApiBaseUrl(connections.activeBaseUrl);
    await refreshNativeServerStatus();
  }
  void refreshConnectionHealth();
  await reloadDbs();
  routeDatabaseListReady.value = true;
  applyRouteDatabaseSelection();
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    await loadSchema(targetDb.value, true);
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [targetDb.value]: true,
    };
  }
  applyPendingExecution();
});

onBeforeUnmount(() => {
  unsubscribeDesktopActions?.();
  window.removeEventListener('keydown', handleStudioShortcut);
  window.removeEventListener('resize', handleWorkbenchResize);
  window.removeEventListener('focus', refreshSchemaOnFocus);
});
</script>

<style scoped>
.workbench-page {
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.workbench-frame {
  position: relative;
  display: grid;
  grid-template-columns: 304px minmax(0, 1fr);
  width: 100%;
  height: 100%;
  min-height: 0;
  background: #fff;
  overflow: hidden;
}

.workbench-frame.is-explorer-collapsed {
  grid-template-columns: 44px minmax(0, 1fr);
}

.workspace-shell,
.query-workspace {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

@media (max-width: 1099px) {
  .workbench-frame {
    grid-template-columns: minmax(0, 1fr);
  }

  .workbench-frame.is-explorer-collapsed {
    grid-template-columns: 44px minmax(0, 1fr);
  }

  .workbench-frame > :deep(.schema-sidebar) {
    position: absolute;
    z-index: 30;
    inset: 0 auto 0 0;
    width: 304px;
    box-shadow: 12px 0 30px rgba(23, 33, 43, 0.12);
  }

  .workbench-frame.is-explorer-collapsed > :deep(.schema-sidebar) {
    position: static;
    width: 44px;
    box-shadow: none;
  }
}
</style>
