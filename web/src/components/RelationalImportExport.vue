<template>
  <section class="relation-import-export">
    <n-empty v-if="!table" description="Select a relation table before importing or exporting." />
    <template v-else>
      <section class="relation-toolband">
        <div>
          <n-text class="relation-section-title">Export table data</n-text>
          <n-text depth="3" class="relation-section-hint">Downloads a bounded SELECT result as CSV or JSON.</n-text>
        </div>
        <n-space size="small" align="center" :wrap="true">
          <n-radio-group v-model:value="exportFormat" size="small">
            <n-radio-button value="csv">CSV</n-radio-button>
            <n-radio-button value="json">JSON</n-radio-button>
          </n-radio-group>
          <n-input-number
            v-model:value="exportLimit"
            size="small"
            :min="1"
            :max="100000"
            :show-button="false"
            class="relation-import-export__limit"
          />
          <n-button size="small" secondary :loading="exportBusy" @click="downloadTableExport">Export</n-button>
        </n-space>
      </section>

      <section class="relation-toolband relation-toolband--import">
        <div>
          <n-text class="relation-section-title">Import rows</n-text>
          <n-text depth="3" class="relation-section-hint">Parse, map columns, dry-run, then commit through staged approval.</n-text>
        </div>
        <n-space size="small" align="center" :wrap="true">
          <n-radio-group v-model:value="importFormat" size="small">
            <n-radio-button value="csv">CSV</n-radio-button>
            <n-radio-button value="json">JSON</n-radio-button>
          </n-radio-group>
          <input
            ref="importFileInput"
            type="file"
            class="relation-import-export__file"
            accept=".csv,.json,.jsonl,.ndjson,text/csv,application/json"
            @change="onImportFileSelected"
          />
          <n-button size="small" secondary @click="pickImportFile">Open file</n-button>
          <n-button size="small" secondary :disabled="!importText.trim()" @click="analyzeImport">Analyze</n-button>
          <n-button size="small" quaternary :disabled="!importText.trim() && !importParsed" @click="clearImport">Clear</n-button>
        </n-space>
      </section>

      <n-input
        v-model:value="importText"
        type="textarea"
        :autosize="{ minRows: 5, maxRows: 10 }"
        placeholder="Paste CSV with a header row, JSON array, or JSON Lines."
        class="relation-import-export__text"
      />

      <section v-if="importParsed" class="relation-import-grid">
        <div class="relation-import-grid__mapping">
          <div class="relation-import-grid__head">
            <n-text class="relation-section-title">Column mapping</n-text>
            <n-tag size="small" :bordered="false">{{ importMappedCount }} mapped</n-tag>
          </div>
          <label v-for="column in tableColumns" :key="column.name" class="relation-mapping-row">
            <span>
              {{ column.name }}
              <small>{{ column.dataType }}{{ column.isNullable ? ' · nullable' : ' · required' }}</small>
            </span>
            <n-select
              :value="importMapping[column.name] ?? ''"
              size="small"
              :options="importHeaderOptions"
              @update:value="updateImportMapping(column.name, String($event))"
            />
          </label>
        </div>

        <div class="relation-import-grid__preview">
          <div class="relation-import-grid__head">
            <n-text class="relation-section-title">Preview</n-text>
            <n-text depth="3">{{ importParsed.rows.length }} rows · {{ importParsed.headers.length }} source columns</n-text>
          </div>
          <n-data-table
            :columns="importSampleColumns"
            :data="importSampleRows"
            :bordered="false"
            :pagination="false"
            size="small"
          />
        </div>
      </section>

      <WriteApprovalPanel
        v-if="importPreviewPlan"
        :plan="importPreviewPlan"
        :busy="importRunning"
        :dry-run-busy="importDryRunBusy"
        @cancel="cancelImportPreview"
        @dry-run="runImportDryRun"
        @confirm="confirmImport"
      />

      <section class="relation-import-actions">
        <n-space size="small" align="center" :wrap="true">
          <n-button size="small" type="primary" :disabled="!importParsed || importRunning" @click="stageImportPreview">
            Stage import
          </n-button>
          <n-button v-if="importRunning" size="small" tertiary type="error" @click="cancelImportRun">
            Cancel import
          </n-button>
        </n-space>
        <n-progress
          v-if="importProgress.total > 0"
          type="line"
          :percentage="importProgressPercent"
          :height="8"
          :show-indicator="true"
          processing
        />
      </section>

      <n-data-table
        v-if="importErrorRows.length"
        :columns="importErrorColumns"
        :data="importErrorRows"
        :bordered="false"
        :pagination="{ pageSize: 8 }"
        size="small"
        class="relation-import-errors"
      />

      <WorkbenchResultPanel
        class="relation-import-export__result"
        title="Import / export result"
        :sql="latestSql"
        :result="latestResult"
        :ran-once="ranOnce"
        :summary="resultSummary"
        :file-name="`${targetDb}_${table.name}_import_export`"
        empty-description="Run an import dry-run, commit, or export to see the result."
        @clear-error="latestResult = null"
      />
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, h, ref } from 'vue';
import {
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NProgress,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpace,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import type { TableInfo } from '@/api/schema';
import {
  execDataSql,
  execDataSqlBatch,
  rowsToObjects,
  type SqlResultSet,
} from '@/api/sql';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';
import {
  buildDefaultImportMapping,
  buildImportStatements,
  exportRowsText,
  parseImportText,
  validateImportRows,
  type ImportMapping,
  type ImportRowError,
  type ImportValidationResult,
  type ParsedImportData,
  type RelationalImportFormat,
} from '@/utils/relationalImportExport';
import {
  downloadText,
  safeFileStem,
  type ResultExportFormat,
} from '@/utils/resultExport';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
} from '@/utils/writeApproval';

const props = defineProps<{
  targetDb: string;
  table: TableInfo | null;
}>();

const emit = defineEmits<{
  refreshSchema: [];
}>();

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const exportFormat = ref<ResultExportFormat>('csv');
const exportLimit = ref(5000);
const exportBusy = ref(false);
const importFormat = ref<RelationalImportFormat>('csv');
const importText = ref('');
const importFileInput = ref<HTMLInputElement | null>(null);
const importParsed = ref<ParsedImportData | null>(null);
const importMapping = ref<ImportMapping>({});
const importValidation = ref<ImportValidationResult | null>(null);
const importPreviewPlan = ref<WriteApprovalPlan | null>(null);
const importDryRunBusy = ref(false);
const importRunning = ref(false);
const importCancelRequested = ref(false);
const importProgress = ref({ done: 0, total: 0 });
const latestResult = ref<SqlResultSet | null>(null);
const latestSql = ref('');
const ranOnce = ref(false);

const tableColumns = computed(() =>
  [...(props.table?.columns ?? [])].sort((a, b) => a.ordinal - b.ordinal));

const importHeaderOptions = computed<SelectOption[]>(() => [
  { label: 'Skip column', value: '' },
  ...(importParsed.value?.headers ?? []).map((header) => ({
    label: header,
    value: header,
  })),
]);

const importMappedCount = computed(() =>
  tableColumns.value.filter((column) => Boolean(importMapping.value[column.name])).length);

const importProgressPercent = computed(() =>
  importProgress.value.total <= 0
    ? 0
    : Math.round((importProgress.value.done / importProgress.value.total) * 100));

const importErrorRows = computed(() =>
  (importValidation.value?.errors ?? importParsed.value?.errors ?? []).map((error, index) => ({
    key: `${error.rowNumber}:${error.column ?? ''}:${index}`,
    ...error,
  })));

const importErrorColumns: DataTableColumns<ImportRowError & { key: string }> = [
  { title: 'Row', key: 'rowNumber', width: 82 },
  { title: 'Column', key: 'column', width: 160 },
  { title: 'Message', key: 'message', minWidth: 280 },
];

const importSampleRows = computed(() =>
  (importParsed.value?.rows ?? []).slice(0, 5).map((row, index) => ({ __key: index, ...row })));

const importSampleColumns = computed<DataTableColumns<Record<string, unknown>>>(() =>
  (importParsed.value?.headers ?? []).slice(0, 8).map((header) => ({
    title: header,
    key: header,
    minWidth: 120,
    ellipsis: { tooltip: true },
    render: (row) => h('span', String(row[header] ?? '')),
  })));

const resultSummary = computed(() => {
  if (!latestResult.value) {
    return importPreviewPlan.value ? importPreviewPlan.value.summary : 'Ready';
  }
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    const affected = latestResult.value.end.recordsAffected >= 0
      ? `affected ${latestResult.value.end.recordsAffected}`
      : `${latestResult.value.end.rowCount} rows`;
    return `${affected} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

function pickImportFile(): void {
  importFileInput.value?.click();
}

async function onImportFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  importText.value = await file.text();
  const name = file.name.toLowerCase();
  importFormat.value = name.endsWith('.json') || name.endsWith('.jsonl') || name.endsWith('.ndjson') ? 'json' : 'csv';
  analyzeImport();
  input.value = '';
}

function analyzeImport(): void {
  const table = props.table;
  if (!table) return;
  const parsed = parseImportText(importFormat.value, importText.value);
  importParsed.value = parsed;
  importMapping.value = buildDefaultImportMapping(table, parsed.headers);
  importValidation.value = null;
  importPreviewPlan.value = null;
  if (parsed.errors.length) {
    message.warning(`${parsed.errors.length} parse issue${parsed.errors.length === 1 ? '' : 's'} found.`);
  } else {
    message.success(`Parsed ${parsed.rows.length} row${parsed.rows.length === 1 ? '' : 's'}.`);
  }
}

function updateImportMapping(column: string, source: string): void {
  importMapping.value = {
    ...importMapping.value,
    [column]: source,
  };
  importValidation.value = null;
  importPreviewPlan.value = null;
}

function runImportDryRun(): void {
  importDryRunBusy.value = true;
  try {
    const validation = validateCurrentImport();
    if (!validation) return;
    if (validation.errors.length > 0) {
      message.error(`${validation.errors.length} import validation issue${validation.errors.length === 1 ? '' : 's'} found.`);
      return;
    }
    message.success(`Dry-run passed for ${validation.rows.length} row${validation.rows.length === 1 ? '' : 's'}.`);
  } finally {
    importDryRunBusy.value = false;
  }
}

function stageImportPreview(): void {
  const validation = validateCurrentImport();
  if (!validation || !props.table) return;
  if (validation.errors.length > 0) {
    message.error('Fix import validation issues before staging.');
    return;
  }
  if (validation.rows.length === 0) {
    message.warning('No rows to import.');
    return;
  }

  const item: WriteApprovalItem = {
    id: `import_${Date.now().toString(36)}`,
    command: `INSERT ${validation.rows.length} row${validation.rows.length === 1 ? '' : 's'} INTO ${formatSqlIdentifier(props.table.name)}`,
    severity: 'write',
    label: 'Import rows',
    detail: `${importMappedCount.value} mapped columns · ${importFormat.value.toUpperCase()}`,
  };
  importPreviewPlan.value = createWriteApprovalPlan({
    id: `import_${props.targetDb}_${props.table.name}_${Date.now().toString(36)}`,
    title: 'Relation import',
    target: `${props.targetDb}.${props.table.name}`,
    items: [item],
    dryRunAvailable: true,
    dryRunLabel: 'Dry-run validation',
  });
}

async function confirmImport(): Promise<void> {
  if (!props.table || !props.targetDb || !importValidation.value || importValidation.value.errors.length > 0) return;

  const rows = importValidation.value.rows;
  const statements = buildImportStatements(props.table, rows, importMapping.value);
  const batchSize = 100;
  importRunning.value = true;
  importCancelRequested.value = false;
  importProgress.value = { done: 0, total: statements.length };
  const errors: ImportRowError[] = [];
  const started = performance.now();

  try {
    for (let offset = 0; offset < statements.length; offset += batchSize) {
      if (importCancelRequested.value) {
        errors.push({ rowNumber: rows[offset]?.rowNumber ?? offset + 1, message: 'Import cancelled before this batch.' });
        break;
      }

      const chunk = statements.slice(offset, offset + batchSize);
      const results = await execDataSqlBatch(auth.api, props.targetDb, [
        { sql: 'BEGIN' },
        ...chunk,
        { sql: 'COMMIT' },
      ]);
      const errorIndex = results.findIndex((result) => result.error);
      if (errorIndex >= 0) {
        const row = rows[offset + Math.max(errorIndex - 1, 0)];
        errors.push({
          rowNumber: row?.rowNumber ?? offset + 1,
          message: results[errorIndex].error?.message ?? 'Import batch failed.',
        });
        break;
      }

      importProgress.value = {
        done: Math.min(offset + chunk.length, statements.length),
        total: statements.length,
      };
    }

    importValidation.value = {
      rows,
      errors,
    };
    const elapsed = performance.now() - started;
    const affected = importProgress.value.done;
    latestSql.value = importPreviewPlan.value?.items[0]?.command ?? '';
    latestResult.value = errors.length > 0
      ? {
        columns: [],
        rows: [],
        hasColumns: false,
        end: null,
        error: {
          type: 'error',
          code: importCancelRequested.value ? 'cancelled' : 'import_failed',
          message: errors[0].message,
        },
      }
      : emptySuccessResult(affected, elapsed);
    ranOnce.value = true;
    recordHistory(errors.length > 0 ? (importCancelRequested.value ? 'cancelled' : 'error') : 'success', latestSql.value, affected, elapsed, errors[0]?.message ?? '');

    if (errors.length > 0) {
      message.warning(errors[0].message);
      return;
    }

    message.success(`Imported ${affected} row${affected === 1 ? '' : 's'}.`);
    importPreviewPlan.value = null;
    emit('refreshSchema');
  } finally {
    importRunning.value = false;
  }
}

function cancelImportRun(): void {
  importCancelRequested.value = true;
}

function cancelImportPreview(): void {
  importPreviewPlan.value = null;
}

function clearImport(): void {
  importText.value = '';
  importParsed.value = null;
  importMapping.value = {};
  importValidation.value = null;
  importPreviewPlan.value = null;
  importProgress.value = { done: 0, total: 0 };
}

async function downloadTableExport(): Promise<void> {
  if (!props.table || !props.targetDb) return;
  exportBusy.value = true;
  const projection = tableColumns.value.map((column) => formatSqlIdentifier(column.name)).join(', ');
  const sql = [
    `SELECT ${projection || '*'}`,
    `FROM ${formatSqlIdentifier(props.table.name)}`,
    `LIMIT ${Math.max(1, Math.trunc(exportLimit.value || 1))};`,
  ].join('\n');
  try {
    const result = await execDataSql(auth.api, props.targetDb, sql);
    latestSql.value = sql;
    latestResult.value = result;
    ranOnce.value = true;
    if (result.error) {
      message.error(result.error.message);
      return;
    }

    const rows = rowsToObjects<Record<string, unknown>>(result);
    const content = exportRowsText(rows, result.columns, exportFormat.value);
    const ext = exportFormat.value === 'json' ? 'json' : 'csv';
    const contentType = exportFormat.value === 'json' ? 'application/json;charset=utf-8' : 'text/csv;charset=utf-8';
    downloadText(`${safeFileStem(`${props.targetDb}_${props.table.name}`, 'table')}.${ext}`, content, contentType);
    message.success(`Exported ${rows.length} row${rows.length === 1 ? '' : 's'}.`);
  } finally {
    exportBusy.value = false;
  }
}

function validateCurrentImport(): ImportValidationResult | null {
  if (!props.table || !importParsed.value) return null;
  const validation = validateImportRows(props.table, importParsed.value, importMapping.value);
  importValidation.value = validation;
  return validation;
}

function recordHistory(
  status: 'success' | 'error' | 'cancelled',
  command: string,
  recordsAffected: number,
  elapsedMs: number,
  error: string,
): void {
  history.record({
    kind: 'operation',
    status,
    title: `${props.table?.name ?? 'table'} import`,
    target: props.table?.name ?? '',
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'table',
    action: 'import',
    command,
    summary: error || `imported ${recordsAffected} rows`,
    recordsAffected,
    elapsedMs,
  });
}

function emptySuccessResult(recordsAffected: number, elapsedMs: number): SqlResultSet {
  return {
    columns: [],
    rows: [],
    hasColumns: false,
    error: null,
    end: {
      type: 'end',
      rowCount: 0,
      recordsAffected,
      elapsedMs,
    },
  };
}
</script>

<style scoped>
.relation-import-export {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  min-height: 0;
  padding: 12px;
  overflow: auto;
  background: #fff;
}

.relation-toolband,
.relation-import-actions {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  min-width: 0;
  padding: 10px 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
  background: #fbfdff;
}

.relation-toolband--import {
  background: #fbfbf6;
}

.relation-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-size: 13px;
  font-weight: 800;
}

.relation-section-hint {
  display: block;
  font-size: 12px;
}

.relation-import-export__limit {
  width: 112px;
}

.relation-import-export__file {
  display: none;
}

.relation-import-export__text {
  flex: 0 0 auto;
}

.relation-import-grid {
  display: grid;
  grid-template-columns: minmax(300px, 0.9fr) minmax(360px, 1.1fr);
  gap: 12px;
}

.relation-import-grid__mapping,
.relation-import-grid__preview {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
  padding: 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
  background: #fff;
}

.relation-import-grid__head,
.relation-mapping-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.relation-mapping-row {
  align-items: center;
}

.relation-mapping-row > span {
  display: flex;
  flex: 0 0 148px;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  color: var(--sndb-ink-strong);
  font-size: 12px;
  font-weight: 800;
}

.relation-mapping-row small {
  overflow: hidden;
  color: var(--sndb-ink-soft);
  font-size: 10px;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-mapping-row :deep(.n-select) {
  min-width: 0;
}

.relation-import-actions {
  align-items: center;
}

.relation-import-actions :deep(.n-progress) {
  max-width: 360px;
}

.relation-import-errors {
  flex: 0 0 auto;
}

.relation-import-export__result {
  flex: 0 0 230px;
  min-height: 210px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
}

@media (max-width: 980px) {
  .relation-toolband,
  .relation-import-actions,
  .relation-import-grid {
    grid-template-columns: 1fr;
    flex-direction: column;
    align-items: stretch;
  }

  .relation-import-actions :deep(.n-progress) {
    max-width: none;
  }
}
</style>
