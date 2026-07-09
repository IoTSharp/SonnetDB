<template>
  <section class="workbench-result-panel">
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
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { NAlert, NButton, NEmpty, NInput, NText, useMessage } from 'naive-ui';
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
</script>

<style scoped>
.workbench-result-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
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
