<template>
  <div class="workspace-tabs">
    <div class="workspace-tabs__scroll" role="tablist" aria-label="工作区对象">
      <button
        v-for="tab in tabs"
        :key="tab.id"
        type="button"
        role="tab"
        class="workspace-tab"
        :class="{ 'is-active': tab.id === activeTabId }"
        :aria-selected="tab.id === activeTabId"
        @click="$emit('select-tab', tab.id)"
      >
        <component :is="tabIcon(tab.tool)" :size="16" :stroke-width="1.8" />
        <span>{{ tab.label }}</span>
        <span
          v-if="tab.closable"
          role="button"
          class="workspace-tab__close"
          :title="`关闭 ${tab.label}`"
          @click.stop="$emit('close-tab', tab.id)"
        ><X :size="14" /></span>
      </button>
      <button type="button" class="workspace-tab workspace-tab--add" title="新建 SQL 查询" @click="$emit('create-tab')">
        <Plus :size="18" />
      </button>
    </div>

    <div class="workspace-tabs__tools">
      <n-button quaternary title="查看结果" @click="$emit('show-result')">
        <template #icon><Rows3 :size="16" /></template>
        结果
      </n-button>
      <n-button quaternary title="查看工作台历史" @click="$emit('show-history')">
        <template #icon><History :size="16" /></template>
        历史
      </n-button>
      <n-button quaternary title="查看慢查询与 Top-N" @click="$emit('show-diagnostics')">
        <template #icon><Gauge :size="16" /></template>
        诊断
      </n-button>
      <n-dropdown trigger="click" :options="connectionOptions" @select="$emit('connection-select', $event)">
        <n-button quaternary class="connection-button">
          <CircleCheck :size="15" />
          {{ connectionName }}
          <ChevronDown :size="14" />
        </n-button>
      </n-dropdown>
      <n-button quaternary title="检查全部连接健康状态" :loading="connectionHealthBusy" @click="$emit('refresh-connection-health')">
        <template #icon><RefreshCw :size="16" /></template>
      </n-button>
      <n-button quaternary title="添加远程连接" @click="$emit('open-connection')">
        <template #icon><PlugZap :size="16" /></template>
      </n-button>

      <div v-if="studioBridgeAvailable" class="native-bridge-controls">
        <span class="native-state" :class="`is-${nativeServerTagType}`">{{ nativeServerLabel }}</span>
        <n-button quaternary :loading="nativeServerBusy" @click="$emit('refresh-native-server')">Health</n-button>
        <n-button
          v-if="!canStopNativeServer"
          secondary
          :loading="nativeServerBusy"
          @click="$emit('start-native-server')"
        >Start</n-button>
        <n-button v-else quaternary :loading="nativeServerBusy" @click="$emit('stop-native-server')">Stop</n-button>
        <n-popover trigger="click" placement="bottom-end" :width="440">
          <template #trigger>
            <n-button quaternary title="配置本地 Server"><Settings2 :size="16" /></n-button>
          </template>
          <div class="native-server-settings">
            <strong>Managed Local Server</strong>
            <span>Data root</span>
            <div class="native-server-settings__row">
              <n-input :value="nativeDataRoot" size="small" placeholder="C:\\SonnetDB\\data" @update:value="$emit('update:native-data-root', $event)" />
              <n-button size="small" secondary @click="$emit('choose-native-data-root')">浏览</n-button>
            </div>
            <small>{{ nativeServerStatus?.url || 'http://127.0.0.1:5080' }}</small>
            <n-button v-if="!canStopNativeServer" type="primary" size="small" :loading="nativeServerBusy" :disabled="!nativeDataRoot.trim()" @click="$emit('start-native-server', nativeDataRoot)">
              使用此目录启动
            </n-button>
          </div>
        </n-popover>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, type Component } from 'vue';
import { NButton, NDropdown, NInput, NPopover, type DropdownOption } from 'naive-ui';
import {
  Archive,
  Braces,
  ChevronDown,
  CircleCheck,
  FileSearch,
  FolderArchive,
  Gauge,
  History,
  Map,
  ActivitySquare,
  MessageSquareMore,
  Network,
  Plus,
  PlugZap,
  RefreshCw,
  Rows3,
  Settings2,
  Table2,
  TerminalSquare,
  X,
} from 'lucide-vue-next';
import type { StudioManagedServerStatus } from '@/api/studioNativeBridge';
import type { WorkbenchTool } from '@/utils/sqlWorkbench';

export interface StudioWorkspaceTab {
  id: string;
  label: string;
  tool: WorkbenchTool;
  db: string;
  objectKey: string;
  closable: boolean;
}

const props = defineProps<{
  tabs: StudioWorkspaceTab[];
  activeTabId: string;
  connectionName: string;
  connectionOptions: DropdownOption[];
  studioBridgeAvailable: boolean;
  nativeServerStatus: StudioManagedServerStatus | null;
  nativeServerBusy: boolean;
  nativeDataRoot: string;
  connectionHealthBusy: boolean;
}>();

defineEmits<{
  'select-tab': [id: string];
  'close-tab': [id: string];
  'create-tab': [];
  'connection-select': [key: string | number];
  'open-connection': [];
  'refresh-native-server': [];
  'refresh-connection-health': [];
  'start-native-server': [dataRoot?: string];
  'stop-native-server': [];
  'choose-native-data-root': [];
  'update:native-data-root': [value: string];
  'show-result': [];
  'show-history': [];
  'show-diagnostics': [];
}>();

const nativeServerLabel = computed(() => {
  if (!props.nativeServerStatus) return 'Studio';
  if (props.nativeServerStatus.healthy) return 'Local healthy';
  if (props.nativeServerStatus.isRunning) return 'Local starting';
  return 'Local stopped';
});

const nativeServerTagType = computed(() => {
  if (!props.nativeServerStatus) return 'info';
  if (props.nativeServerStatus.healthy) return 'success';
  if (props.nativeServerStatus.isRunning) return 'warning';
  return 'default';
});

const canStopNativeServer = computed(() => Boolean(props.nativeServerStatus?.startedByStudio));

function tabIcon(tool: WorkbenchTool): Component {
  return {
    sql: TerminalSquare,
    trajectory: Map,
    measurement: ActivitySquare,
    table: Table2,
    document: Braces,
    kv: Archive,
    mq: MessageSquareMore,
    vector: Network,
    fulltext: FileSearch,
    bucket: FolderArchive,
  }[tool];
}
</script>

<style scoped>
.workspace-tabs {
  display: flex;
  flex: 0 0 44px;
  align-items: stretch;
  min-width: 0;
  border-bottom: 1px solid var(--sndb-border);
  background: var(--sndb-chrome);
}

.workspace-tabs__scroll {
  display: flex;
  flex: 1;
  min-width: 0;
  overflow-x: auto;
  scrollbar-width: thin;
}

.workspace-tab {
  position: relative;
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 8px;
  min-width: 132px;
  max-width: 220px;
  height: 44px;
  padding: 0 10px 0 13px;
  border: 0;
  border-right: 1px solid var(--sndb-border);
  background: transparent;
  color: var(--sndb-ink-muted);
  font: inherit;
  cursor: pointer;
}

.workspace-tab > span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workspace-tab:hover {
  background: var(--sndb-hover);
}

.workspace-tab.is-active {
  background: #fff;
  color: var(--sndb-ink-strong);
}

.workspace-tab.is-active::after {
  position: absolute;
  right: 0;
  bottom: 0;
  left: 0;
  height: 2px;
  background: var(--sndb-interactive);
  content: '';
}

.workspace-tab__close {
  display: grid;
  flex: 0 0 24px;
  place-items: center;
  width: 24px;
  height: 24px;
  margin-left: auto;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--sndb-ink-subtle);
  cursor: pointer;
}

.workspace-tab__close:hover {
  background: #e8ebee;
  color: var(--sndb-ink-strong);
}

.workspace-tab--add {
  justify-content: center;
  min-width: 44px;
  width: 44px;
  padding: 0;
}

.workspace-tabs__tools,
.native-bridge-controls {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 2px;
}

.native-server-settings {
  display: grid;
  gap: 10px;
}

.native-server-settings > span,
.native-server-settings > small {
  color: #667085;
  font-size: 12px;
}

.native-server-settings__row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
}

.workspace-tabs__tools {
  padding: 3px 7px;
  border-left: 1px solid var(--sndb-border);
  background: #fff;
}

.connection-button :deep(.n-button__content) {
  gap: 6px;
}

.connection-button :deep(svg:first-child) {
  color: var(--sndb-success);
}

.native-state {
  padding: 2px 7px;
  color: var(--sndb-ink-muted);
  font-size: 12px;
}

.native-state.is-success {
  color: var(--sndb-success);
}

.native-state.is-warning {
  color: var(--sndb-warning);
}

@media (max-width: 1100px) {
  .workspace-tabs__tools .connection-button,
  .native-state,
  .native-bridge-controls .n-button:first-of-type {
    display: none;
  }
}

@media (max-width: 720px) {
  .workspace-tabs__tools {
    display: none;
  }

  .workspace-tab {
    min-width: 116px;
  }
}
</style>
