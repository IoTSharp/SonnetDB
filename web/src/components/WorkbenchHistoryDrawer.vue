<template>
  <n-drawer
    :show="show"
    width="420"
    placement="right"
    @update:show="$emit('update:show', $event)"
  >
    <n-drawer-content title="工作台历史" closable>
      <div class="workbench-history">
        <div class="workbench-history__toolbar">
          <n-text depth="3">{{ filteredEntries.length }} 条记录</n-text>
          <n-popconfirm @positive-click="history.clear">
            <template #trigger>
              <n-button size="small" quaternary :disabled="history.entries.length === 0">清除</n-button>
            </template>
            清除本机保存的工作台历史？
          </n-popconfirm>
        </div>

        <div class="workbench-history__filters">
          <n-input v-model:value="keyword" size="small" clearable placeholder="筛选操作、对象或命令" />
          <n-select v-model:value="modelFilter" size="small" :options="modelOptions" />
          <n-select v-model:value="statusFilter" size="small" :options="statusOptions" />
        </div>

        <n-empty v-if="filteredEntries.length === 0" description="当前筛选范围内没有历史记录。" />

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
                恢复
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
import { computed, ref } from 'vue';
import {
  NButton,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NInput,
  NPopconfirm,
  NScrollbar,
  NSelect,
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
const keyword = ref('');
const modelFilter = ref('');
const statusFilter = ref('');

const modelOptions = computed(() => [
  { label: '全部模型', value: '' },
  ...Array.from(new Set(history.entries.map((entry) => entry.model).filter(Boolean)))
    .sort()
    .map((model) => ({ label: model, value: model })),
]);

const statusOptions = [
  { label: '全部状态', value: '' },
  { label: '成功', value: 'success' },
  { label: '失败', value: 'error' },
  { label: '预检', value: 'dry-run' },
  { label: '已取消', value: 'cancelled' },
];

const filteredEntries = computed(() => {
  const db = normalizeDatabase(props.activeDatabase);
  const query = keyword.value.trim().toLowerCase();
  return history.recentEntries.filter((entry) => {
    if (db && entry.database !== db) return false;
    if (modelFilter.value && entry.model !== modelFilter.value) return false;
    if (statusFilter.value && entry.status !== statusFilter.value) return false;
    if (!query) return true;
    return [entry.title, entry.target, entry.action, entry.command, entry.summary]
      .some((value) => value.toLowerCase().includes(query));
  });
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

.workbench-history__filters {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 120px 110px;
  gap: 8px;
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
  border-radius: var(--sndb-radius);
  background: #fff;
}

@media (max-width: 520px) {
  .workbench-history__filters {
    grid-template-columns: 1fr;
  }
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
