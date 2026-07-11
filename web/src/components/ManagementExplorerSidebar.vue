<template>
  <aside class="schema-sidebar" :class="{ 'is-collapsed': collapsed }">
    <template v-if="collapsed">
      <button type="button" class="collapsed-tool" title="展开资源浏览器" @click="$emit('toggle-collapse')">
        <PanelLeftOpen :size="19" />
      </button>
      <button type="button" class="collapsed-tool" title="刷新资源" @click="$emit('refresh')">
        <RefreshCw :size="18" :class="{ 'is-spinning': loadingSchema || loadingDbs }" />
      </button>
      <div class="collapsed-label">资源浏览器</div>
      <span class="collapsed-health" title="数据库健康" />
    </template>

    <template v-else>
      <header class="explorer-header">
        <strong>资源浏览器</strong>
        <div class="explorer-header__actions">
          <button
            v-if="isSuperuser"
            type="button"
            class="icon-button"
            title="新建数据库"
            :disabled="databaseActionBusy"
            @click="$emit('open-create-database')"
          >
            <Plus :size="18" />
          </button>
          <button type="button" class="icon-button" title="刷新资源" @click="$emit('refresh')">
            <RefreshCw :size="17" :class="{ 'is-spinning': loadingSchema || loadingDbs }" />
          </button>
          <button type="button" class="icon-button" title="维护与更多操作" @click="showMaintenance = true">
            <MoreHorizontal :size="19" />
          </button>
          <button type="button" class="icon-button" title="收起资源浏览器" @click="$emit('toggle-collapse')">
            <PanelLeftClose :size="18" />
          </button>
        </div>
      </header>

      <div class="explorer-toolbar">
        <n-input
          :value="schemaFilter"
          clearable
          placeholder="搜索资源"
          class="explorer-search"
          @update:value="$emit('update:schemaFilter', $event)"
        >
          <template #prefix><Search :size="16" /></template>
        </n-input>
        <button type="button" class="icon-button explorer-filter" title="按名称过滤">
          <ListFilter :size="17" />
        </button>
      </div>

      <div class="explorer-viewbar">
        <div class="view-switch" role="group" aria-label="资源视图">
          <button
            type="button"
            :class="{ 'is-active': viewMode === 'simple' }"
            title="简洁视图"
            @click="viewMode = 'simple'"
          ><ListTree :size="17" /></button>
          <button
            type="button"
            :class="{ 'is-active': viewMode === 'detailed' }"
            title="详细视图"
            @click="viewMode = 'detailed'"
          ><Rows3 :size="17" /></button>
        </div>
        <span>按名称</span>
        <ChevronDown :size="15" />
      </div>

      <n-scrollbar class="schema-tree" trigger="none">
        <n-alert v-if="databaseTree.length === 0" type="info" :show-icon="false" class="schema-empty-note">
          {{ databasesLength === 0 ? '暂无可用数据库' : '没有匹配的数据库' }}
        </n-alert>

        <section class="schema-group schema-group--databases">
          <button type="button" class="schema-group__head" @click="$emit('toggle-group', 'databases')">
            <ChevronDown v-if="openGroups.databases" :size="16" />
            <ChevronRight v-else :size="16" />
            <Database :size="17" />
            <span>数据库</span>
            <small>{{ databaseTree.length }}</small>
          </button>

          <div v-if="openGroups.databases" class="schema-group__items">
            <button
              v-if="isSuperuser && systemTreeNode"
              type="button"
              class="schema-item schema-item--database"
              :class="{ 'is-active': targetDb === CONTROL_PLANE_KEY }"
              :title="systemTreeNode.meta"
              @click="$emit('select-database', CONTROL_PLANE_KEY)"
            >
              <span class="schema-item__spacer" />
              <ServerCog :size="17" />
              <span class="schema-item__content">
                <strong>{{ systemTreeNode.name }}</strong>
                <small v-if="viewMode === 'detailed'">{{ systemTreeNode.meta }}</small>
              </span>
            </button>

            <div v-for="dbNode in databaseTree" :key="dbNode.name" class="schema-database-node">
              <button
                type="button"
                class="schema-item schema-item--database"
                :class="{ 'is-active': targetDb === dbNode.name }"
                :title="dbNode.meta"
                @click="$emit('select-database', dbNode.name)"
              >
                <span class="schema-item__caret" @click.stop="$emit('toggle-database', dbNode.name)">
                  <ChevronDown v-if="expandedDatabases[dbNode.name]" :size="15" />
                  <ChevronRight v-else :size="15" />
                </span>
                <Database :size="17" />
                <span class="schema-item__content">
                  <strong>{{ dbNode.name }}</strong>
                  <small v-if="viewMode === 'detailed'">{{ dbNode.meta }}</small>
                </span>
              </button>

              <div v-if="expandedDatabases[dbNode.name]" class="schema-group__items schema-group__items--children">
                <div
                  v-for="group in explorerGroups(dbNode)"
                  :key="`${dbNode.name}:${group.key}`"
                  class="schema-child-block"
                >
                  <button
                    type="button"
                    class="schema-child-block__head"
                    @click="toggleModelGroup(dbNode.name, group.key)"
                  >
                    <ChevronDown v-if="isModelGroupOpen(dbNode.name, group.key)" :size="14" />
                    <ChevronRight v-else :size="14" />
                    <component :is="groupIcon(group.key)" :size="16" />
                    <span>{{ group.label }}</span>
                    <small>{{ group.count }}</small>
                  </button>

                  <div v-if="isModelGroupOpen(dbNode.name, group.key)" class="schema-model-items">
                    <button
                      v-for="item in group.items"
                      :key="`${dbNode.name}:${item.key}`"
                      type="button"
                      class="schema-item schema-item--object"
                      :class="[item.className, { 'is-active': targetDb === dbNode.name && activeExplorerKey === item.key }]"
                      :title="item.title"
                      @click="$emit('select-item', dbNode.name, item)"
                      @dblclick="$emit('open-item', dbNode.name, item)"
                      @contextmenu.prevent="$emit('context-menu', $event, dbNode.name, item)"
                    >
                      <span class="schema-item__spacer" />
                      <component :is="modelIcon(item.model)" :size="15" />
                      <span class="schema-item__content">
                        <strong>{{ item.name }}</strong>
                        <small v-if="viewMode === 'detailed'">{{ item.meta }}</small>
                      </span>
                    </button>
                  </div>
                </div>

                <div v-if="explorerGroups(dbNode).length === 0" class="schema-group__empty">
                  {{ dbNode.emptyText }}
                </div>
              </div>
            </div>
          </div>
        </section>
      </n-scrollbar>

      <button type="button" class="database-health" @click="showMaintenance = true">
        <span class="database-health__state"><i />数据库健康度</span>
        <strong>{{ maintenanceStatus ? '需查看' : '健康' }}</strong>
        <ChevronRight :size="16" />
      </button>
    </template>

    <n-drawer v-model:show="showMaintenance" :width="360" placement="left">
      <n-drawer-content title="数据库维护" closable>
        <div class="maintenance-panel">
          <div class="maintenance-target">
            <Database :size="18" />
            <div>
              <strong>{{ targetDb === CONTROL_PLANE_KEY ? 'system' : (targetDb || '未选择数据库') }}</strong>
              <span>健康检查、索引重建与备份校验</span>
            </div>
          </div>

          <div class="maintenance-actions">
            <n-button :loading="maintenanceBusy === 'health_check'" :disabled="!targetDb || targetDb === CONTROL_PLANE_KEY" @click="$emit('health-check')">
              运行健康检查
            </n-button>
            <n-button :loading="maintenanceBusy.startsWith('rebuild:')" :disabled="!selectedIndex" @click="$emit('rebuild-index')">
              重建选中索引
            </n-button>
          </div>

          <label class="maintenance-field">
            <span>备份目录</span>
            <div class="maintenance-path-row">
              <n-input
                :value="backupDirectory"
                clearable
                placeholder="输入备份目录"
                @update:value="$emit('update:backupDirectory', $event)"
              />
              <n-button v-if="studioBridgeAvailable" title="选择备份目录" @click="$emit('pick-backup-directory')"><FolderOpen :size="16" /></n-button>
            </div>
          </label>
          <label class="maintenance-field">
            <span>恢复演练目标目录</span>
            <div class="maintenance-path-row">
              <n-input
                :value="restoreTargetDirectory"
                clearable
                placeholder="输入目标目录"
                @update:value="$emit('update:restoreTargetDirectory', $event)"
              />
              <n-button v-if="studioBridgeAvailable" title="选择恢复目标目录" @click="$emit('pick-restore-directory')"><FolderOpen :size="16" /></n-button>
            </div>
          </label>

          <div class="maintenance-actions">
            <n-button :loading="maintenanceBusy === 'backup_verify'" :disabled="!backupDirectory.trim() || !isSuperuser" @click="$emit('verify-backup')">
              校验备份
            </n-button>
            <n-button type="primary" :loading="maintenanceBusy === 'restore_dry_run'" :disabled="!backupDirectory.trim() || !restoreTargetDirectory.trim() || !isSuperuser" @click="$emit('restore-dry-run')">
              恢复演练
            </n-button>
          </div>

          <n-alert v-if="maintenanceStatus" type="info" :show-icon="false">
            {{ maintenanceStatus }}
          </n-alert>

          <n-popconfirm
            v-if="isSuperuser"
            :show-icon="false"
            :positive-button-props="{ type: 'error' }"
            :disabled="!canDropDatabase"
            @positive-click="$emit('drop-database')"
          >
            <template #trigger>
              <n-button type="error" secondary :disabled="!canDropDatabase || databaseActionBusy">
                删除当前数据库
              </n-button>
            </template>
            <span>确定删除数据库 {{ targetDb || '(none)' }}？</span>
          </n-popconfirm>
        </div>
      </n-drawer-content>
    </n-drawer>
  </aside>
</template>

<script setup lang="ts">
import { ref, type Component } from 'vue';
import { NAlert, NButton, NDrawer, NDrawerContent, NInput, NPopconfirm, NScrollbar } from 'naive-ui';
import {
  Archive,
  Braces,
  ChevronDown,
  ChevronRight,
  Database,
  FileSearch,
  FolderArchive,
  FolderOpen,
  ListFilter,
  ListTree,
  MessageSquareMore,
  MoreHorizontal,
  Network,
  PanelLeftClose,
  PanelLeftOpen,
  Plus,
  RefreshCw,
  Rows3,
  Search,
  ServerCog,
  Table2,
  Waves,
} from 'lucide-vue-next';
import { CONTROL_PLANE_KEY } from '@/stores/sqlConsole';
import type { DatabaseTreeNode, ExplorerGroup, ExplorerItem, ExplorerModel, SystemTreeNode } from '@/utils/managementExplorer';
import type { IndexLifecycleInfo } from '@/api/schema';

const props = defineProps<{
  collapsed: boolean;
  isSuperuser: boolean;
  targetDb: string;
  databaseTree: DatabaseTreeNode[];
  databasesLength: number;
  systemTreeNode: SystemTreeNode | null;
  openGroups: Record<string, boolean>;
  expandedDatabases: Record<string, boolean>;
  activeExplorerKey: string;
  schemaFilter: string;
  loadingSchema: boolean;
  loadingDbs: boolean;
  databaseActionBusy: boolean;
  canDropDatabase: boolean;
  backupDirectory: string;
  restoreTargetDirectory: string;
  maintenanceBusy: string;
  maintenanceStatus: string;
  selectedIndex: IndexLifecycleInfo | null;
  studioBridgeAvailable: boolean;
  explorerGroups: (dbNode: DatabaseTreeNode) => ExplorerGroup[];
}>();

defineEmits<{
  'toggle-collapse': [];
  'open-create-database': [];
  'drop-database': [];
  refresh: [];
  'update:schemaFilter': [value: string];
  'toggle-group': [key: string];
  'select-database': [db: string];
  'toggle-database': [db: string];
  'select-item': [db: string, item: ExplorerItem];
  'open-item': [db: string, item: ExplorerItem];
  'context-menu': [event: MouseEvent, db: string, item: ExplorerItem];
  'health-check': [];
  'rebuild-index': [];
  'update:backupDirectory': [value: string];
  'update:restoreTargetDirectory': [value: string];
  'verify-backup': [];
  'restore-dry-run': [];
  'pick-backup-directory': [];
  'pick-restore-directory': [];
}>();

type ViewMode = 'simple' | 'detailed';
const viewMode = ref<ViewMode>('simple');
const showMaintenance = ref(false);
const modelGroups = ref<Record<string, boolean>>({});

function activeGroup(): string {
  const key = props.activeExplorerKey;
  if (key.startsWith('table:')) return 'tables';
  if (key.startsWith('document:')) return 'documents';
  if (key.startsWith('kv:')) return 'kv';
  if (key.startsWith('vector:')) return 'vector';
  if (key.startsWith('fulltext:')) return 'fulltext';
  if (key.startsWith('mq:')) return 'mq';
  if (key.startsWith('bucket:')) return 'buckets';
  if (key === 'backup-status') return 'backup';
  return 'measurements';
}

function isModelGroupOpen(db: string, group: string): boolean {
  const key = `${db}:${group}`;
  if (key in modelGroups.value) return Boolean(modelGroups.value[key]);
  return db === props.targetDb && group === activeGroup();
}

function toggleModelGroup(db: string, group: string): void {
  const key = `${db}:${group}`;
  modelGroups.value = { ...modelGroups.value, [key]: !isModelGroupOpen(db, group) };
}

function groupIcon(group: string): Component {
  return {
    measurements: Waves,
    tables: Table2,
    documents: Braces,
    kv: Archive,
    indexes: ServerCog,
    vector: Network,
    fulltext: FileSearch,
    mq: MessageSquareMore,
    buckets: FolderArchive,
    backup: Archive,
  }[group] ?? Database;
}

function modelIcon(model: ExplorerModel): Component {
  return groupIcon({
    measurement: 'measurements',
    table: 'tables',
    document: 'documents',
    kv: 'kv',
    index: 'indexes',
    vector: 'vector',
    fulltext: 'fulltext',
    mq: 'mq',
    bucket: 'buckets',
    backup: 'backup',
  }[model]);
}
</script>

<style scoped>
.schema-sidebar {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  border-right: 1px solid var(--sndb-border);
  background: rgba(253, 253, 254, 0.98);
}

.schema-sidebar.is-collapsed {
  align-items: center;
  gap: 6px;
  padding: 8px 0;
}

.collapsed-tool,
.icon-button {
  display: inline-grid;
  place-items: center;
  width: 34px;
  height: 34px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: 5px;
  background: transparent;
  color: var(--sndb-ink-soft);
  cursor: pointer;
}

.collapsed-tool:hover,
.icon-button:hover {
  border-color: var(--sndb-border);
  background: var(--sndb-hover);
  color: var(--sndb-ink-strong);
}

.collapsed-label {
  margin: 10px 0 auto;
  color: var(--sndb-ink-muted);
  font-size: 12px;
  letter-spacing: 0;
  writing-mode: vertical-rl;
}

.collapsed-health {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: var(--sndb-success);
}

.explorer-header,
.explorer-toolbar,
.explorer-viewbar {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
}

.explorer-header {
  justify-content: space-between;
  min-height: 48px;
  padding: 0 9px 0 15px;
}

.explorer-header strong {
  font-size: 15px;
  font-weight: 650;
}

.explorer-header__actions {
  display: flex;
  align-items: center;
}

.explorer-toolbar {
  gap: 6px;
  padding: 8px 12px;
  border-top: 1px solid var(--sndb-border);
  border-bottom: 1px solid var(--sndb-border);
}

.explorer-search {
  flex: 1;
}

.explorer-filter {
  flex: 0 0 auto;
  border-color: var(--sndb-border);
}

.explorer-viewbar {
  gap: 6px;
  min-height: 44px;
  padding: 5px 12px;
  border-bottom: 1px solid var(--sndb-border);
  color: var(--sndb-ink-muted);
  font-size: 13px;
}

.explorer-viewbar > span {
  margin-left: auto;
}

.view-switch {
  display: inline-flex;
  border: 1px solid var(--sndb-border);
  border-radius: 5px;
  overflow: hidden;
}

.view-switch button {
  display: grid;
  place-items: center;
  width: 34px;
  height: 30px;
  border: 0;
  border-right: 1px solid var(--sndb-border);
  background: #fff;
  color: var(--sndb-ink-muted);
  cursor: pointer;
}

.view-switch button:last-child {
  border-right: 0;
}

.view-switch button.is-active {
  background: var(--sndb-interactive);
  color: #fff;
}

.schema-tree {
  flex: 1;
  min-height: 0;
  padding: 8px 7px 12px 9px;
}

.schema-empty-note {
  margin: 0 3px 8px;
}

.schema-group__head,
.schema-child-block__head,
.schema-item {
  display: flex;
  align-items: center;
  width: 100%;
  min-width: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--sndb-ink-strong);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.schema-group__head {
  gap: 7px;
  min-height: 40px;
  padding: 0 8px;
  font-weight: 650;
}

.schema-group__head span,
.schema-child-block__head span {
  flex: 1;
  min-width: 0;
}

.schema-group__head small,
.schema-child-block__head small {
  color: var(--sndb-ink-subtle);
  font-size: 12px;
}

.schema-group__items--children {
  padding-left: 12px;
}

.schema-child-block__head {
  gap: 7px;
  min-height: 40px;
  padding: 0 8px;
  color: #38434b;
}

.schema-item {
  gap: 7px;
  min-height: 40px;
  padding: 4px 8px;
}

.schema-item:hover,
.schema-group__head:hover,
.schema-child-block__head:hover {
  background: var(--sndb-hover);
}

.schema-item.is-active {
  background: var(--sndb-selected);
  color: #0b4f86;
}

.schema-item__caret,
.schema-item__spacer {
  display: grid;
  flex: 0 0 15px;
  place-items: center;
}

.schema-item__content {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

.schema-item__content strong,
.schema-item__content small {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.schema-item__content strong {
  font-size: 14px;
  font-weight: 450;
}

.schema-item__content small {
  color: var(--sndb-ink-muted);
  font-size: 12px;
}

.schema-item--database .schema-item__content strong {
  font-weight: 600;
}

.schema-model-items {
  padding-left: 14px;
}

.schema-group__empty {
  padding: 8px 10px;
  color: var(--sndb-ink-muted);
  font-size: 13px;
}

.database-health {
  display: grid;
  grid-template-columns: 1fr auto 18px;
  align-items: center;
  gap: 8px;
  min-height: 52px;
  padding: 0 14px;
  border: 0;
  border-top: 1px solid var(--sndb-border);
  background: #fff;
  color: var(--sndb-ink-soft);
  font: inherit;
  cursor: pointer;
}

.database-health:hover {
  background: var(--sndb-hover);
}

.database-health__state {
  display: flex;
  align-items: center;
  gap: 8px;
}

.database-health__state i {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: var(--sndb-success);
}

.database-health strong {
  padding: 2px 6px;
  border-radius: 4px;
  background: #e9f5e9;
  color: var(--sndb-success);
  font-size: 12px;
}

.maintenance-panel,
.maintenance-actions,
.maintenance-field {
  display: flex;
  flex-direction: column;
}

.maintenance-path-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 36px;
  gap: 8px;
}

.maintenance-panel {
  gap: 18px;
}

.maintenance-target {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding-bottom: 16px;
  border-bottom: 1px solid var(--sndb-border);
}

.maintenance-target div {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.maintenance-target span,
.maintenance-field > span {
  color: var(--sndb-ink-muted);
  font-size: 13px;
}

.maintenance-actions {
  gap: 8px;
}

.maintenance-field {
  gap: 6px;
}

.is-spinning {
  animation: spin 0.8s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}
</style>
