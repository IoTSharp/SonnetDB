<template>
  <aside class="schema-sidebar">
    <div class="schema-toolbar">
      <div v-if="isSuperuser" class="schema-toolbar__create">
        <n-button size="small" type="primary" :loading="databaseActionBusy" @click="$emit('open-create-database')">
          Create
        </n-button>
        <n-popconfirm
          :show-icon="false"
          :positive-button-props="{ type: 'error' }"
          :negative-button-props="{ tertiary: true }"
          :disabled="!canDropDatabase"
          @positive-click="$emit('drop-database')"
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
        <n-button size="small" quaternary :loading="loadingSchema || loadingDbs" title="Refresh databases" @click="$emit('refresh')">
          R
        </n-button>
        <n-input
          :value="schemaFilter"
          size="small"
          clearable
          placeholder="Search databases / objects"
          class="schema-toolbar__search"
          @update:value="$emit('update:schemaFilter', $event)"
        />
      </div>
    </div>

    <n-scrollbar class="schema-tree">
      <n-alert v-if="databaseTree.length === 0" type="info" :show-icon="false" class="schema-empty-note">
        {{ databasesLength === 0 ? 'No databases available yet.' : 'No databases match this filter.' }}
      </n-alert>

      <section class="schema-group schema-group--databases">
        <button type="button" class="schema-group__head" @click="$emit('toggle-group', 'databases')">
          <span class="schema-group__caret">{{ openGroups.databases ? 'v' : '>' }}</span>
          <span class="schema-group__icon">DB</span>
          <span class="schema-group__label">DATABASES</span>
          <span class="schema-group__count">{{ databaseTree.length }}</span>
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
              @click="$emit('select-database', dbNode.name)"
            >
              <span
                class="schema-item__caret"
                @click.stop="$emit('toggle-database', dbNode.name)"
              >{{ expandedDatabases[dbNode.name] ? 'v' : '>' }}</span>
              <span class="schema-item__name">{{ dbNode.name }}</span>
              <span class="schema-item__meta">{{ dbNode.meta }}</span>
            </button>

            <div v-if="expandedDatabases[dbNode.name]" class="schema-group__items schema-group__items--children">
              <div
                v-for="group in explorerGroups(dbNode)"
                :key="`${dbNode.name}:${group.key}`"
                class="schema-child-block"
              >
                <div class="schema-child-block__head">
                  <span>{{ group.label }}</span>
                  <span>{{ group.count }}</span>
                </div>
                <button
                  v-for="item in group.items"
                  :key="`${dbNode.name}:${item.key}`"
                  type="button"
                  class="schema-item"
                  :class="[item.className, { 'is-active': targetDb === dbNode.name && activeExplorerKey === item.key }]"
                  :title="item.title"
                  @click="$emit('select-item', dbNode.name, item)"
                  @dblclick="$emit('open-item', dbNode.name, item)"
                  @contextmenu.prevent="$emit('context-menu', $event, dbNode.name, item)"
                >
                  <span class="schema-item__name">{{ item.name }}</span>
                  <span class="schema-item__meta">{{ item.meta }}</span>
                </button>
              </div>

              <div v-if="explorerGroups(dbNode).length === 0" class="schema-group__empty">
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
        <n-button size="tiny" tertiary :loading="maintenanceBusy === 'health_check'" :disabled="!targetDb || targetDb === CONTROL_PLANE_KEY" @click="$emit('health-check')">
          Health
        </n-button>
        <n-button size="tiny" tertiary :loading="maintenanceBusy.startsWith('rebuild:')" :disabled="!selectedIndex" @click="$emit('rebuild-index')">
          Rebuild
        </n-button>
      </div>
      <n-input
        :value="backupDirectory"
        size="tiny"
        clearable
        placeholder="Backup directory"
        @update:value="$emit('update:backupDirectory', $event)"
      />
      <n-input
        :value="restoreTargetDirectory"
        size="tiny"
        clearable
        placeholder="Restore dry-run target"
        @update:value="$emit('update:restoreTargetDirectory', $event)"
      />
      <div class="maintenance-panel__actions">
        <n-button size="tiny" tertiary :loading="maintenanceBusy === 'backup_verify'" :disabled="!backupDirectory.trim() || !isSuperuser" @click="$emit('verify-backup')">
          Verify
        </n-button>
        <n-button size="tiny" tertiary :loading="maintenanceBusy === 'restore_dry_run'" :disabled="!backupDirectory.trim() || !restoreTargetDirectory.trim() || !isSuperuser" @click="$emit('restore-dry-run')">
          Dry-run
        </n-button>
      </div>
      <p v-if="maintenanceStatus" class="maintenance-panel__result">
        {{ maintenanceStatus }}
      </p>
    </section>
  </aside>
</template>

<script setup lang="ts">
import { NAlert, NButton, NInput, NPopconfirm, NScrollbar } from 'naive-ui';
import { CONTROL_PLANE_KEY } from '@/stores/sqlConsole';
import type {
  DatabaseTreeNode,
  ExplorerGroup,
  ExplorerItem,
  SystemTreeNode,
} from '@/utils/managementExplorer';
import type { IndexLifecycleInfo } from '@/api/schema';

defineProps<{
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
  explorerGroups: (dbNode: DatabaseTreeNode) => ExplorerGroup[];
}>();

defineEmits<{
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
}>();
</script>

<style scoped>
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
  font-size: 10px;
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

.schema-item--kv,
.schema-item--document,
.schema-item--vector,
.schema-item--fulltext,
.schema-item--mq,
.schema-item--bucket {
  border-left: 2px solid transparent;
}

.schema-item--document {
  border-left-color: rgba(32, 128, 240, 0.55);
}

.schema-item--kv {
  border-left-color: rgba(24, 160, 88, 0.55);
}

.schema-item--vector {
  border-left-color: rgba(176, 92, 24, 0.55);
}

.schema-item--fulltext {
  border-left-color: rgba(131, 86, 210, 0.55);
}

.schema-item--mq {
  border-left-color: rgba(32, 128, 240, 0.55);
}

.schema-item--bucket {
  border-left-color: rgba(13, 59, 102, 0.45);
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

@media (max-width: 1280px) {
  .schema-sidebar {
    border-right: 0;
    border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  }
}
</style>
