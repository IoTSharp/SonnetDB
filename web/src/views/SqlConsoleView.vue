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
          @update:native-data-root="setNativeDataRoot"
          @show-result="toggleResultDrawer"
          @show-history="globalHistoryVisible = true"
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
        @open-trajectory="setWorkbenchTool('trajectory')"
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
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { NDropdown, useMessage } from 'naive-ui';
import CreateDatabaseDialog from '@/components/CreateDatabaseDialog.vue';
import DocumentCollectionWorkbench from '@/components/DocumentCollectionWorkbench.vue';
import FullTextSearchWorkbench from '@/components/FullTextSearchWorkbench.vue';
import KvKeyspaceWorkbench from '@/components/KvKeyspaceWorkbench.vue';
import ManagementExplorerSidebar from '@/components/ManagementExplorerSidebar.vue';
import ObjectBucketWorkbench from '@/components/ObjectBucketWorkbench.vue';
import RelationalTableWorkbench from '@/components/RelationalTableWorkbench.vue';
import RemoteConnectionDialog from '@/components/RemoteConnectionDialog.vue';
import SonnetMqWorkbench from '@/components/SonnetMqWorkbench.vue';
import SqlQueryWorkspace from '@/components/SqlQueryWorkspace.vue';
import StudioWorkspaceTabs, { type StudioWorkspaceTab } from '@/components/StudioWorkspaceTabs.vue';
import VectorSearchWorkbench from '@/components/VectorSearchWorkbench.vue';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import TrajectoryMap from '@/views/TrajectoryMap.vue';
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
import type { WorkbenchTool } from '@/utils/sqlWorkbench';

const auth = useAuthStore();
const connections = useConnectionsStore();
const sqlConsole = useSqlConsoleStore();
const workbenchHistory = useWorkbenchHistoryStore();
const message = useMessage();
const explorerCollapsed = ref(false);
const globalHistoryVisible = ref(false);
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

const activeObjectIdentity = computed(() => {
  switch (activeWorkbenchTool.value) {
    case 'table':
      return { label: selectedTable.value?.name ?? '关系表', key: selectedTable.value ? `table:${selectedTable.value.name}` : 'table' };
    case 'document':
      return { label: selectedDocumentCollection.value?.name ?? '文档集合', key: selectedDocumentCollection.value ? `document:${selectedDocumentCollection.value.name}` : 'document' };
    case 'kv':
      return { label: selectedKvKeyspace.value || 'KV Keyspace', key: selectedKvKeyspace.value ? `kv:${selectedKvKeyspace.value}` : 'kv' };
    case 'mq':
      return { label: selectedMqTopic.value || 'MQ Topic', key: selectedMqTopic.value ? `mq:${selectedMqTopic.value}` : 'mq' };
    case 'vector': {
      const index = selectedVectorIndex.value;
      return { label: index ? `${index.measurement}.${index.column}` : '向量索引', key: index ? vectorIndexKey(index) : 'vector' };
    }
    case 'fulltext': {
      const index = selectedFullTextIndex.value;
      return { label: index ? `${index.collection}.${index.name}` : '全文索引', key: index ? fullTextIndexKey(index) : 'fulltext' };
    }
    case 'bucket':
      return { label: selectedObjectBucket.value || '对象桶', key: selectedObjectBucket.value ? `bucket:${selectedObjectBucket.value}` : 'bucket' };
    case 'trajectory':
      return { label: '轨迹分析', key: activeExplorerKey.value || 'trajectory' };
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

watch(() => connections.activeProfileId, async () => {
  auth.setApiBaseUrl(connections.activeBaseUrl);
  resetExplorerCache();
  if (connections.activeDatabase) {
    targetDb.value = connections.activeDatabase;
  }
  await reloadDbs();
  if (targetDb.value && targetDb.value !== CONTROL_PLANE_KEY) {
    expandedDatabases.value = {
      ...expandedDatabases.value,
      [targetDb.value]: true,
    };
    await loadSchema(targetDb.value, true);
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

onMounted(async () => {
  unsubscribeDesktopActions = subscribeStudioDesktopActions(handleStudioDesktopAction);
  window.addEventListener('keydown', handleStudioShortcut);
  const bridgeReady = await connections.connectStudioBridge();
  if (bridgeReady) {
    auth.setApiBaseUrl(connections.activeBaseUrl);
    await refreshNativeServerStatus();
  }
  void refreshConnectionHealth();
  await reloadDbs();
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
