<template>
  <Teleport to="body">
  <section
    v-show="open"
    class="workbench-result-panel"
    data-workbench-result-drawer
    :style="{ left: `${drawerLeft}px` }"
  >
    <div class="workbench-result-panel__toolbar">
      <div class="workbench-result-panel__identity">
        <n-text class="workbench-result-panel__title">{{ title }}</n-text>
        <n-text depth="3" class="workbench-result-panel__subtitle">{{ headerText }}</n-text>
      </div>
      <div class="workbench-result-panel__actions">
        <n-input
          v-model:value="filterText"
          size="small"
          clearable
          placeholder="Search result"
          class="workbench-result-panel__search"
        />
        <slot name="actions" />
        <n-button size="small" quaternary title="Copy result set as CSV" :disabled="!canExport" @click="copyCsv">
          CSV
        </n-button>
        <n-button size="small" quaternary title="Export result set as CSV" :disabled="!canExport" @click="downloadCsv">
          Export
        </n-button>
        <n-button size="small" quaternary title="Export result set as JSON" :disabled="!canExport" @click="downloadJson">
          JSON
        </n-button>
        <n-button size="small" quaternary title="关闭结果" @click="open = false">
          <template #icon><X :size="16" /></template>
        </n-button>
      </div>
    </div>

    <div class="workbench-result-panel__body">
      <n-alert
        v-if="errorMessage && !result?.error"
        type="error"
        :title="errorMessage"
        closable
        class="workbench-result-panel__alert"
        @close="$emit('clear-error')"
      />

      <SqlResultPanel
        v-if="result?.hasColumns || result?.error"
        class="workbench-result-panel__result"
        :index="0"
        :sql="sql"
        :result="result"
        :display-rows="displayRows"
      />

      <n-empty v-else-if="ranOnce" description="Statement executed without rows." />
      <n-empty v-else :description="emptyDescription" />
    </div>

    <div class="workbench-result-panel__status">
      <span>{{ footerText }}</span>
      <span>{{ filteredRows.length }} rows</span>
    </div>
  </section>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { NAlert, NButton, NEmpty, NInput, NText, useMessage } from 'naive-ui';
import { X } from 'lucide-vue-next';
import type { SqlResultSet } from '@/api/sql';
import SqlResultPanel from '@/components/SqlResultPanel.vue';
import {
  buildCsv,
  buildJson,
  copyText,
  resultRowsToObjects,
  safeFileStem,
  saveTextFile,
} from '@/utils/resultExport';
import { formatSqlValue } from '@/utils/sqlValue';

const props = withDefaults(defineProps<{
  title?: string;
  sql?: string;
  result: SqlResultSet | null;
  summary?: string;
  errorMessage?: string;
  ranOnce?: boolean;
  emptyDescription?: string;
  fileName?: string;
}>(), {
  title: 'Results',
  sql: '',
  summary: '',
  errorMessage: '',
  ranOnce: false,
  emptyDescription: 'Run an operation to see results.',
  fileName: 'result',
});

defineEmits<{
  'clear-error': [];
}>();

const message = useMessage();
const filterText = ref('');
const open = ref(false);
const drawerLeft = ref(368);

const rows = computed(() => resultRowsToObjects(props.result));
const columns = computed(() => props.result?.columns ?? []);

const filteredRows = computed(() => {
  const keyword = filterText.value.trim().toLowerCase();
  if (!keyword) return rows.value;
  return rows.value.filter((row) =>
    columns.value.some((column) => formatSqlValue(row[column]).toLowerCase().includes(keyword)));
});

const displayRows = computed(() =>
  columns.value.length === 0
    ? []
    : filteredRows.value.map((row) => columns.value.map((column) => row[column])));

const canExport = computed(() => Boolean(props.result?.hasColumns && columns.value.length > 0));

const headerText = computed(() => {
  if (props.result?.error) {
    return `Error · ${props.result.error.code ?? 'error'}`;
  }
  if (props.result?.end) {
    return `Executed in ${props.result.end.elapsedMs.toFixed(2)} ms`;
  }
  return props.summary || 'Ready';
});

const footerText = computed(() => {
  if (props.result?.error) {
    return props.result.error.message;
  }
  if (props.result?.end) {
    const parts: string[] = [];
    if (props.result.hasColumns) {
      parts.push(`${props.result.end.rowCount} rows`);
    }
    if (props.result.end.recordsAffected >= 0) {
      parts.push(`affected ${props.result.end.recordsAffected}`);
    }
    parts.push(`${props.result.end.elapsedMs.toFixed(2)} ms`);
    return parts.join(' · ');
  }
  return props.ranOnce ? (props.summary || 'Statement executed.') : 'Ready';
});

async function copyCsv(): Promise<void> {
  if (!canExport.value) return;
  const ok = await copyText(buildCsv(filteredRows.value, columns.value));
  if (ok) {
    message.success('CSV copied');
  } else {
    message.warning('Clipboard is unavailable');
  }
}

async function downloadCsv(): Promise<void> {
  if (!canExport.value) return;
  try {
    const outcome = await saveTextFile(
      `${safeFileStem(props.fileName, 'result')}.csv`,
      buildCsv(filteredRows.value, columns.value),
      'text/csv;charset=utf-8',
    );
    if (outcome === 'native') message.success('CSV saved');
  } catch (error) {
    message.error(error instanceof Error ? error.message : 'CSV export failed');
  }
}

async function downloadJson(): Promise<void> {
  if (!canExport.value) return;
  try {
    const outcome = await saveTextFile(
      `${safeFileStem(props.fileName, 'result')}.json`,
      buildJson(filteredRows.value, columns.value),
      'application/json;charset=utf-8',
    );
    if (outcome === 'native') message.success('JSON saved');
  } catch (error) {
    message.error(error instanceof Error ? error.message : 'JSON export failed');
  }
}

function onToggleResult(event: Event): void {
  const detail = (event as CustomEvent<{ open?: boolean }>).detail;
  updateDrawerOffset();
  open.value = detail?.open ?? !open.value;
}

function updateDrawerOffset(): void {
  if (window.innerWidth <= 840) {
    drawerLeft.value = 56;
    return;
  }
  const workspace = document.querySelector<HTMLElement>('.workspace-shell');
  drawerLeft.value = Math.round(workspace?.getBoundingClientRect().left ?? 368);
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && open.value) open.value = false;
}

onMounted(() => {
  window.addEventListener('sndb:toggle-result', onToggleResult);
  window.addEventListener('keydown', onKeydown);
  window.addEventListener('resize', updateDrawerOffset);
});

onBeforeUnmount(() => {
  window.removeEventListener('sndb:toggle-result', onToggleResult);
  window.removeEventListener('keydown', onKeydown);
  window.removeEventListener('resize', updateDrawerOffset);
});
</script>

<style scoped>
.workbench-result-panel {
  position: fixed;
  z-index: 850;
  right: 0;
  bottom: 0;
  left: 368px;
  display: flex;
  height: min(340px, 55vh);
  flex-direction: column;
  min-height: 0;
  border-top: 1px solid var(--sndb-border-strong);
  background: #fff;
  box-shadow: 0 -12px 30px rgba(23, 33, 43, 0.1);
}

.workbench-result-panel__toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.workbench-result-panel__identity {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.workbench-result-panel__title {
  font-size: 12px;
  font-weight: 700;
  color: #345;
}

.workbench-result-panel__subtitle {
  overflow: hidden;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.workbench-result-panel__actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.workbench-result-panel__search {
  width: 180px;
}

.workbench-result-panel__body {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.workbench-result-panel__body :deep(.n-empty) {
  margin: 24px;
}

.workbench-result-panel__alert,
.workbench-result-panel__result {
  margin: 12px;
}

.workbench-result-panel__status {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  background: #fafcff;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

@media (max-width: 840px) {
  .workbench-result-panel {
    height: min(420px, 64vh);
  }

  .workbench-result-panel__toolbar {
    flex-direction: column;
    align-items: stretch;
  }

  .workbench-result-panel__actions {
    justify-content: flex-start;
  }

  .workbench-result-panel__search {
    width: 100%;
  }
}
</style>
