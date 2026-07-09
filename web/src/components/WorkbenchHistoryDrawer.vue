<template>
  <n-drawer
    :show="show"
    width="460"
    placement="right"
    @update:show="$emit('update:show', $event)"
  >
    <n-drawer-content title="Workbench history" closable>
      <div class="workbench-history">
        <div class="workbench-history__toolbar">
          <n-text depth="3">{{ filteredEntries.length }} entries</n-text>
          <n-button size="small" quaternary :disabled="history.entries.length === 0" @click="history.clear">
            Clear
          </n-button>
        </div>

        <n-empty v-if="filteredEntries.length === 0" description="No history yet." />

        <n-scrollbar v-else class="workbench-history__scroll">
          <article
            v-for="entry in filteredEntries"
            :key="entry.id"
            class="workbench-history-entry"
          >
            <div class="workbench-history-entry__head">
              <div class="workbench-history-entry__title">
                <n-space size="small" align="center" :wrap="true">
                  <n-tag size="tiny" :type="statusType(entry.status)" :bordered="false">
                    {{ entry.status }}
                  </n-tag>
                  <n-tag size="tiny" :bordered="false">
                    {{ entry.kind }}
                  </n-tag>
                </n-space>
                <strong>{{ entry.title }}</strong>
              </div>
              <n-button
                v-if="entry.command"
                size="tiny"
                secondary
                @click="$emit('select', entry)"
              >
                Open
              </n-button>
            </div>
            <n-text depth="3" class="workbench-history-entry__meta">
              {{ formatTime(entry.createdAt) }} · {{ entry.connectionName || 'connection' }} / {{ entry.database || 'system' }}
            </n-text>
            <p class="workbench-history-entry__summary">{{ entry.summary || entry.action }}</p>
            <code v-if="entry.command">{{ entry.command }}</code>
          </article>
        </n-scrollbar>
      </div>
    </n-drawer-content>
  </n-drawer>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import {
  NButton,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NScrollbar,
  NSpace,
  NTag,
  NText,
} from 'naive-ui';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
  type WorkbenchHistoryStatus,
} from '@/stores/workbenchHistory';

const props = withDefaults(defineProps<{
  show: boolean;
  activeDatabase?: string;
}>(), {
  activeDatabase: '',
});

defineEmits<{
  'update:show': [value: boolean];
  select: [entry: WorkbenchHistoryEntry];
}>();

const history = useWorkbenchHistoryStore();

const filteredEntries = computed(() => {
  const db = normalizeDatabase(props.activeDatabase);
  if (!db) return history.recentEntries;
  return history.recentEntries.filter((entry) => entry.database === db);
});

function normalizeDatabase(value: string): string {
  return value === '__control_plane__' ? 'system' : value;
}

function statusType(status: WorkbenchHistoryStatus): 'success' | 'error' | 'warning' | 'default' {
  if (status === 'success') return 'success';
  if (status === 'error') return 'error';
  if (status === 'dry-run') return 'warning';
  return 'default';
}

function formatTime(value: number): string {
  return new Date(value).toLocaleString();
}
</script>

<style scoped>
.workbench-history {
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: 100%;
}

.workbench-history__toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.workbench-history__scroll {
  flex: 1;
  min-height: 0;
}

.workbench-history-entry {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  margin-bottom: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 8px;
  background: #fff;
}

.workbench-history-entry__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.workbench-history-entry__title {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.workbench-history-entry__title strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workbench-history-entry__meta {
  font-size: 12px;
}

.workbench-history-entry__summary {
  margin: 0;
  color: var(--sndb-ink-soft);
  font-size: 12px;
  line-height: 1.45;
}

.workbench-history-entry code {
  display: block;
  max-height: 96px;
  overflow: auto;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
