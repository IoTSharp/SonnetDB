<template>
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
        <div class="connection-switcher">
          <n-dropdown trigger="click" :options="connectionOptions" @select="$emit('connection-select', $event)">
            <n-button size="small" secondary>
              {{ connectionName }}
            </n-button>
          </n-dropdown>
          <n-button size="small" quaternary @click="$emit('open-connection')">
            Remote
          </n-button>
        </div>

        <div class="workbench-mode-switch" role="tablist" aria-label="Studio mode">
          <button
            type="button"
            class="workbench-mode-switch__button"
            :class="{ 'is-active': activeTool === 'sql' }"
            @click="$emit('set-tool', 'sql')"
          >
            SQL
          </button>
          <button
            type="button"
            class="workbench-mode-switch__button"
            :class="{ 'is-active': activeTool === 'trajectory' }"
            @click="$emit('set-tool', 'trajectory')"
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
</template>

<script setup lang="ts">
import type { DropdownOption } from 'naive-ui';
import { NButton, NDropdown, NTag, NText } from 'naive-ui';
import type { WorkbenchTool } from '@/utils/sqlWorkbench';

export interface AccessBadge {
  label: string;
  type: 'default' | 'info' | 'success' | 'warning' | 'error';
}

defineProps<{
  connectionLabel: string;
  connectionName: string;
  connectionOptions: DropdownOption[];
  activeTool: WorkbenchTool;
  accessBadges: AccessBadge[];
}>();

defineEmits<{
  'connection-select': [key: string | number];
  'open-connection': [];
  'set-tool': [tool: WorkbenchTool];
}>();
</script>

<style scoped>
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

.workbench-context__tools {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.workbench-mode-switch {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 8px;
  background: #fff;
}

.workbench-mode-switch__button {
  min-width: 86px;
  padding: 5px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--sndb-ink-soft);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.workbench-mode-switch__button.is-active {
  background: rgba(44, 123, 229, 0.12);
  color: var(--sndb-ink-strong);
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

.connection-switcher {
  display: flex;
  align-items: center;
  gap: 6px;
  justify-content: flex-end;
}

@media (max-width: 840px) {
  .workbench-context__main {
    flex-direction: column;
    align-items: stretch;
  }

  .workbench-context__badges {
    justify-content: flex-start;
  }
}
</style>
