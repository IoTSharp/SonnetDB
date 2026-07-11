<template>
  <main class="measurement-workbench" data-testid="workbench-measurement">
    <section class="measurement-toolbar">
      <div class="measurement-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="success" :bordered="false">MEASUREMENT</n-tag>
          <n-text class="measurement-toolbar__title">{{ measurement?.name ?? 'No measurement selected' }}</n-text>
          <n-tag v-if="tagColumns.length" size="tiny" :bordered="false">{{ tagColumns.length }} TAG</n-tag>
          <n-tag v-if="fieldColumns.length" size="tiny" :bordered="false">{{ fieldColumns.length }} FIELD</n-tag>
        </n-space>
        <n-text depth="3" class="measurement-toolbar__meta">
          {{ targetDb || 'database' }} · 点级编辑、文件导入与目标级实时监控
        </n-text>
      </div>
      <n-space v-if="activeView === 'points'" size="small" align="center" :wrap="true">
        <n-button size="small" secondary :loading="loadingPoints" @click="loadPoints">刷新</n-button>
        <n-button size="small" quaternary :disabled="pointRows.length === 0" @click="exportVisiblePoints('csv')">导出 CSV</n-button>
        <n-button size="small" quaternary :disabled="pointRows.length === 0" @click="exportVisiblePoints('json')">导出 JSON</n-button>
        <n-button size="small" type="primary" :disabled="!measurement" @click="openNewPoint">新增数据点</n-button>
        <n-button size="small" quaternary @click="historyVisible = true">历史</n-button>
      </n-space>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeView"
      :items="sections"
      aria-label="时序数据工作区"
      @update:model-value="activeView = $event as MeasurementView"
    />

    <WriteApprovalPanel
      v-if="approvalPlan"
      :plan="approvalPlan"
      :busy="writeBusy"
      :abortable="pendingOperations.some((item) => item.action === 'import')"
      @cancel="clearPendingOperations"
      @confirm="confirmPendingOperations"
      @abort="importCancelRequested = true"
    />

    <template v-if="activeView === 'points'">
      <section class="measurement-filterbar">
        <label>
          <span>起始时间</span>
          <input v-model="fromTime" type="datetime-local" />
        </label>
        <label>
          <span>结束时间</span>
          <input v-model="toTime" type="datetime-local" />
        </label>
        <label>
          <span>TAG</span>
          <n-select v-model:value="filterTag" size="small" clearable :options="tagFilterOptions" placeholder="全部 series" />
        </label>
        <label>
          <span>TAG 值</span>
          <n-input v-model:value="filterTagValue" size="small" clearable placeholder="精确匹配" :disabled="!filterTag" />
        </label>
        <label class="measurement-filterbar__limit">
          <span>行数</span>
          <n-select v-model:value="pointLimit" size="small" :options="limitOptions" />
        </label>
        <n-button size="small" secondary :loading="loadingPoints" @click="loadPoints">查询</n-button>
      </section>

      <n-alert v-if="errorMessage" type="error" :title="errorMessage" closable class="measurement-alert" @close="errorMessage = ''" />

      <section v-if="editorOpen && measurement" class="point-editor">
        <header class="point-editor__head">
          <div>
            <n-text class="point-editor__title">{{ editingSource ? '校正数据点' : '新增数据点' }}</n-text>
            <n-text depth="3">写入先进入暂存审批；校正必须改变 time 或 TAG，再删除原身份并写入新点。</n-text>
          </div>
          <n-space size="small">
            <n-button size="small" quaternary @click="closeEditor">取消</n-button>
            <n-button size="small" type="primary" @click="stagePoint">{{ editingSource ? '暂存校正' : '暂存新增' }}</n-button>
          </n-space>
        </header>
        <div class="point-editor__grid">
          <label v-for="column in columns" :key="column.name" class="point-field" :class="`is-${columnRole(column)}`">
            <span class="point-field__label">
              {{ column.name }}
              <small>{{ column.role }} · {{ column.dataType }}</small>
            </span>
            <n-select
              v-if="normalizedMeasurementType(column) === 'boolean'"
              :value="booleanDraftValue(pointDraft[column.name])"
              size="small"
              :options="booleanOptions"
              @update:value="pointDraft[column.name] = $event === 'true'"
            />
            <n-input-number
              v-else-if="normalizedMeasurementType(column) === 'int64' && columnRole(column) !== 'time'"
              :value="numberDraftValue(pointDraft[column.name])"
              size="small"
              :show-button="false"
              @update:value="pointDraft[column.name] = $event"
            />
            <n-input-number
              v-else-if="normalizedMeasurementType(column) === 'float64'"
              :value="numberDraftValue(pointDraft[column.name])"
              size="small"
              :show-button="false"
              @update:value="pointDraft[column.name] = $event"
            />
            <n-input
              v-else
              :value="draftText(pointDraft[column.name])"
              size="small"
              :disabled="normalizedMeasurementType(column) === 'vector'"
              :placeholder="columnRole(column) === 'time' ? 'Unix ms / ISO 8601' : column.dataType"
              @update:value="pointDraft[column.name] = $event"
            />
          </label>
        </div>
        <n-alert v-if="pointValidationErrors.length" type="warning" :show-icon="false">
          {{ pointValidationErrors.map((item) => `${item.column ? `${item.column}: ` : ''}${item.message}`).join('；') }}
        </n-alert>
      </section>

      <section class="measurement-grid-shell">
        <n-empty v-if="!measurement" description="请从资源浏览器选择 Measurement。" />
        <n-data-table
          v-else
          :columns="pointTableColumns"
          :data="pointRows"
          :loading="loadingPoints || loading"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="(row: PointGridRow) => row.__key"
          size="small"
          flex-height
          class="measurement-grid"
        />
      </section>
      <footer class="measurement-statusbar">
        <span>{{ pointSummary }}</span>
        <code v-if="lastPointSql">{{ lastPointSql }}</code>
      </footer>
    </template>

    <section v-else-if="activeView === 'import'" class="measurement-import">
      <section class="measurement-toolband">
        <div>
          <n-text class="measurement-section-title">Measurement 文件导入</n-text>
          <n-text depth="3">CSV / JSON / JSONL，自动映射列、类型校验、分批提交并记录操作历史。</n-text>
        </div>
        <n-space size="small" align="center" :wrap="true">
          <n-radio-group v-model:value="importFormat" size="small">
            <n-radio-button value="csv">CSV</n-radio-button>
            <n-radio-button value="json">JSON / JSONL</n-radio-button>
          </n-radio-group>
          <input ref="fileInput" type="file" class="measurement-file-input" accept=".csv,.json,.jsonl,.ndjson,text/csv,application/json" @change="onFileSelected" />
          <n-button size="small" secondary @click="fileInput?.click()">选择文件</n-button>
          <n-button size="small" type="primary" :disabled="!importText.trim()" @click="analyzeImport">解析</n-button>
          <n-button size="small" quaternary @click="clearImport">清空</n-button>
        </n-space>
      </section>

      <n-input v-model:value="importText" type="textarea" :autosize="{ minRows: 4, maxRows: 8 }" placeholder="粘贴 CSV、JSON 数组或 JSONL 数据" />

      <section v-if="importParsed && measurement" class="measurement-import-grid">
        <div class="measurement-import-map">
          <header>
            <n-text class="measurement-section-title">列映射</n-text>
            <n-text depth="3">{{ importParsed.rows.length }} 行 · {{ importParsed.headers.length }} 个源列</n-text>
          </header>
          <label v-for="column in columns" :key="column.name">
            <span>{{ column.name }} <small>{{ column.role }} · {{ column.dataType }}</small></span>
            <n-select
              :value="importMapping[column.name] ?? ''"
              size="small"
              :options="importHeaderOptions"
              @update:value="updateImportMapping(column.name, String($event))"
            />
          </label>
        </div>
        <div class="measurement-import-preview">
          <header>
            <n-text class="measurement-section-title">文件预览</n-text>
            <n-text depth="3">最多显示前 8 行</n-text>
          </header>
          <n-data-table :columns="importPreviewColumns" :data="importPreviewRows" :bordered="false" :pagination="false" size="small" />
        </div>
      </section>

      <section class="measurement-import-actions">
        <n-space size="small" align="center" :wrap="true">
          <n-button size="small" secondary :disabled="!importParsed" @click="validateImportOnly">校验</n-button>
          <n-button size="small" type="primary" :disabled="!importParsed || importBusy" @click="stageImport">暂存导入</n-button>
        </n-space>
        <n-progress v-if="importProgress.total" type="line" :percentage="importProgressPercent" :height="8" processing />
      </section>

      <n-data-table
        v-if="importErrors.length"
        :columns="importErrorColumns"
        :data="importErrors"
        :bordered="false"
        :pagination="{ pageSize: 8 }"
        size="small"
        class="measurement-import-errors"
      />
    </section>

    <section v-else-if="activeView === 'monitor'" class="measurement-monitor">
      <section class="monitor-controls">
        <n-radio-group v-model:value="monitorModel" size="small">
          <n-radio-button value="measurement">Measurement</n-radio-button>
          <n-radio-button value="table">关系表</n-radio-button>
        </n-radio-group>
        <n-select v-model:value="monitorTarget" size="small" :options="monitorTargetOptions" class="monitor-controls__target" />
        <n-select v-model:value="monitorInterval" size="small" :options="intervalOptions" class="monitor-controls__interval" />
        <n-select v-model:value="monitorLimit" size="small" :options="monitorLimitOptions" class="monitor-controls__interval" />
        <n-button size="small" secondary :loading="monitorLoading" @click="refreshMonitor(true)">立即刷新</n-button>
        <n-button size="small" :type="monitorRunning ? 'warning' : 'primary'" @click="toggleMonitor">
          <template #icon><Pause v-if="monitorRunning" :size="15" /><Play v-else :size="15" /></template>
          {{ monitorRunning ? '暂停' : '开始' }}
        </n-button>
      </section>

      <section class="monitor-stats">
        <div><span>状态</span><strong :class="monitorRunning ? 'is-live' : ''">{{ monitorRunning ? 'LIVE' : 'PAUSED' }}</strong></div>
        <div><span>最近刷新</span><strong>{{ monitorUpdatedLabel }}</strong></div>
        <div><span>返回行</span><strong>{{ monitorRows.length }}</strong></div>
        <div><span>查询耗时</span><strong>{{ monitorElapsedLabel }}</strong></div>
      </section>

      <n-alert v-if="monitorError" type="error" :title="monitorError" closable @close="monitorError = ''" />
      <section class="monitor-chart-panel">
        <header>
          <div>
            <n-text class="measurement-section-title">实时趋势</n-text>
            <n-text depth="3">{{ monitorModel === 'measurement' ? '单 Measurement' : '单关系表' }} · 仅保留当前目标的最近结果</n-text>
          </div>
          <code>{{ monitorSql }}</code>
        </header>
        <SqlResultChart v-if="monitorResult?.hasColumns" :columns="monitorResult.columns" :rows="monitorRows" />
        <n-empty v-else description="选择目标并开始监控。" />
      </section>
      <section class="monitor-grid-panel">
        <n-data-table :columns="monitorTableColumns" :data="monitorGridRows" :loading="monitorLoading" :bordered="false" :pagination="false" size="small" flex-height />
      </section>
    </section>

    <section v-else class="measurement-schema">
      <header>
        <div>
          <n-text class="measurement-section-title">Measurement Schema</n-text>
          <n-text depth="3">时序身份由 time + TAG 确定，FIELD 存放可采样值。</n-text>
        </div>
        <n-button size="small" secondary @click="emit('openSql', `DESCRIBE MEASUREMENT ${formatSqlIdentifier(measurement?.name ?? '')}`)">在 SQL 中查看</n-button>
      </header>
      <n-data-table :columns="schemaTableColumns" :data="columns" :bordered="false" :pagination="false" size="small" />
    </section>

    <WorkbenchHistoryDrawer v-model:show="historyVisible" :active-database="targetDb" />
  </main>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, reactive, ref, watch } from 'vue';
import {
  NAlert,
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
import { Pause, Play } from 'lucide-vue-next';
import type { ColumnInfo, MeasurementInfo, TableInfo } from '@/api/schema';
import {
  execDataSql,
  execDataSqlBatch,
  rowsToObjects,
  sqlParameterFromValue,
  type SqlParameters,
  type SqlResultSet,
  type SqlStatementRequest,
} from '@/api/sql';
import SqlResultChart from '@/components/SqlResultChart.vue';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';
import {
  buildMeasurementDeleteStatement,
  buildMeasurementImportMapping,
  buildMeasurementInsertStatements,
  columnRole,
  measurementColumns,
  normalizedMeasurementType,
  parseMeasurementImport,
  validateMeasurementImport,
  validateMeasurementPoint,
  type ImportRowError,
  type MeasurementImportFormat,
  type MeasurementImportMapping,
  type MeasurementImportValidation,
  type ParsedImportData,
} from '@/utils/measurementImport';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';
import { formatSqlValue } from '@/utils/sqlValue';
import { buildCsv, buildJson, safeFileStem, saveTextFile } from '@/utils/resultExport';
import { createWriteApprovalPlan, type WriteApprovalPlan } from '@/utils/writeApproval';

const props = withDefaults(defineProps<{
  targetDb: string;
  measurement: MeasurementInfo | null;
  measurements?: MeasurementInfo[];
  tables?: TableInfo[];
  loading?: boolean;
}>(), {
  measurements: () => [],
  tables: () => [],
  loading: false,
});

const emit = defineEmits<{
  openSql: [sql: string];
  refreshSchema: [];
}>();

type MeasurementView = 'points' | 'import' | 'monitor' | 'schema';
type MonitorModel = 'measurement' | 'table';
interface PointGridRow extends Record<string, unknown> { __key: string; __row: number }
interface PendingOperation {
  id: string;
  action: 'insert' | 'replace' | 'delete' | 'import';
  label: string;
  detail: string;
  statements: SqlStatementRequest[];
  rowCount: number;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();
const activeView = ref<MeasurementView>('points');
const sections: WorkbenchSectionTab[] = [
  { key: 'points', label: '数据点' },
  { key: 'import', label: '文件导入' },
  { key: 'monitor', label: '实时监控' },
  { key: 'schema', label: 'Schema' },
];

const columns = computed(() => measurementColumns(props.measurement));
const tagColumns = computed(() => columns.value.filter((column) => columnRole(column) === 'tag'));
const fieldColumns = computed(() => columns.value.filter((column) => columnRole(column) === 'field'));
const pointResult = ref<SqlResultSet | null>(null);
const loadingPoints = ref(false);
const errorMessage = ref('');
const lastPointSql = ref('');
const fromTime = ref('');
const toTime = ref('');
const filterTag = ref<string | null>(null);
const filterTagValue = ref('');
const pointLimit = ref(100);
const editorOpen = ref(false);
const editingSource = ref<Record<string, unknown> | null>(null);
const pointDraft = reactive<Record<string, unknown>>({});
const pointValidationErrors = ref<ImportRowError[]>([]);
const pendingOperations = ref<PendingOperation[]>([]);
const writeBusy = ref(false);
const historyVisible = ref(false);

const limitOptions: SelectOption[] = [25, 50, 100, 250, 500].map((value) => ({ label: `${value} 行`, value }));
const booleanOptions: SelectOption[] = [
  { label: 'TRUE', value: 'true' },
  { label: 'FALSE', value: 'false' },
];
const tagFilterOptions = computed<SelectOption[]>(() => tagColumns.value.map((column) => ({ label: column.name, value: column.name })));
const pointRows = computed<PointGridRow[]>(() => (pointResult.value ? rowsToObjects<Record<string, unknown>>(pointResult.value) : []).map((row, index) => ({
  ...row,
  __key: `${String(row.time ?? '')}:${tagColumns.value.map((column) => String(row[column.name] ?? '')).join(':')}:${index}`,
  __row: index + 1,
})));
const pointSummary = computed(() => {
  if (pointResult.value?.error) return pointResult.value.error.message;
  if (pointResult.value?.end) return `${pointRows.value.length} 个点 · ${pointResult.value.end.elapsedMs.toFixed(2)} ms`;
  return props.measurement ? '准备查询' : '未选择 Measurement';
});

const pointTableColumns = computed<DataTableColumns<PointGridRow>>(() => [
  { title: '#', key: '__row', width: 54, fixed: 'left' },
  ...columns.value.map((column) => ({
    title: () => h('div', { class: 'measurement-column-title' }, [
      h('strong', column.name),
      h('small', `${column.role.toUpperCase()} · ${column.dataType}`),
    ]),
    key: column.name,
    minWidth: columnRole(column) === 'time' ? 170 : 130,
    ellipsis: { tooltip: true },
    render: (row: PointGridRow) => renderPointValue(row[column.name], column),
  })),
  {
    title: '操作',
    key: '__actions',
    width: 148,
    fixed: 'right',
    render: (row: PointGridRow) => h(NSpace, { size: 4, wrap: false }, { default: () => [
      h(NButton, { size: 'tiny', secondary: true, onClick: () => openEditPoint(row) }, { default: () => '校正' }),
      h(NButton, { size: 'tiny', tertiary: true, type: 'error', onClick: () => stageDelete(row) }, { default: () => '删除' }),
    ] }),
  },
]);

const approvalPlan = computed<WriteApprovalPlan | null>(() => {
  if (!props.measurement || pendingOperations.value.length === 0) return null;
  return createWriteApprovalPlan({
    id: `measurement_${props.targetDb}_${props.measurement.name}_${Date.now().toString(36)}`,
    title: pendingOperations.value.some((item) => item.action === 'import') ? 'Measurement import' : 'Measurement point changes',
    target: `${props.targetDb}.${props.measurement.name}`,
    items: pendingOperations.value.map((operation) => ({
      id: operation.id,
      command: operation.action === 'import'
        ? `INSERT ${operation.rowCount} POINTS INTO ${formatSqlIdentifier(props.measurement!.name)}`
        : operation.statements.map((statement) => statement.sql).join('\n'),
      severity: operation.action === 'delete' || operation.action === 'replace' ? 'danger' : 'write',
      label: operation.label,
      detail: operation.detail,
    })),
  });
});

async function loadPoints(): Promise<void> {
  if (!props.measurement || !props.targetDb) return;
  loadingPoints.value = true;
  errorMessage.value = '';
  const parameters: SqlParameters = { limit: sqlParameterFromValue(pointLimit.value) };
  const predicates: string[] = [];
  if (fromTime.value) {
    parameters.from = sqlParameterFromValue(Date.parse(fromTime.value));
    predicates.push(`${formatSqlIdentifier('time')} >= @from`);
  }
  if (toTime.value) {
    parameters.to = sqlParameterFromValue(Date.parse(toTime.value));
    predicates.push(`${formatSqlIdentifier('time')} <= @to`);
  }
  if (filterTag.value && filterTagValue.value) {
    parameters.tag = sqlParameterFromValue(filterTagValue.value);
    predicates.push(`${formatSqlIdentifier(filterTag.value)} = @tag`);
  }
  const sql = [
    `SELECT ${columns.value.map((column) => formatSqlIdentifier(column.name)).join(', ')}`,
    `FROM ${formatSqlIdentifier(props.measurement.name)}`,
    predicates.length ? `WHERE ${predicates.join(' AND ')}` : '',
    `ORDER BY ${formatSqlIdentifier('time')} DESC`,
    'LIMIT @limit;',
  ].filter(Boolean).join('\n');
  lastPointSql.value = sql;
  try {
    const result = await execDataSql(auth.api, props.targetDb, sql, parameters);
    pointResult.value = result;
    if (result.error) errorMessage.value = result.error.message;
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '加载数据点失败。';
  } finally {
    loadingPoints.value = false;
  }
}

function openNewPoint(): void {
  resetPointDraft();
  pointDraft.time = new Date().toISOString();
  editingSource.value = null;
  editorOpen.value = true;
}

function openEditPoint(row: PointGridRow): void {
  resetPointDraft();
  for (const column of columns.value) {
    pointDraft[column.name] = normalizedMeasurementType(column) === 'vector'
      ? undefined
      : row[column.name];
  }
  editingSource.value = Object.fromEntries(columns.value.map((column) => [column.name, row[column.name]]));
  editorOpen.value = true;
}

function closeEditor(): void {
  editorOpen.value = false;
  editingSource.value = null;
  pointValidationErrors.value = [];
}

function resetPointDraft(): void {
  for (const key of Object.keys(pointDraft)) delete pointDraft[key];
  pointValidationErrors.value = [];
}

function stagePoint(): void {
  if (!props.measurement) return;
  const validation = validateMeasurementPoint(props.measurement, pointDraft);
  pointValidationErrors.value = validation.errors;
  if (validation.errors.length > 0) return;
  if (editingSource.value && hasSamePointIdentity(editingSource.value, validation.values)) {
    pointValidationErrors.value = [{
      rowNumber: 1,
      message: '校正必须改变 time 或至少一个 TAG；同身份重写会被原点 tombstone 持续屏蔽。',
    }];
    return;
  }
  const insert = buildMeasurementInsertStatements(props.measurement, [{ rowNumber: 1, values: validation.values }])[0];
  const replacing = editingSource.value !== null;
  const statements = replacing
    ? [buildMeasurementDeleteStatement(props.measurement, editingSource.value!), insert]
    : [insert];
  pendingOperations.value.push({
    id: `point_${Date.now().toString(36)}`,
    action: replacing ? 'replace' : 'insert',
    label: replacing ? 'Correct point identity' : 'Insert point',
    detail: identityLabel(validation.values),
    statements,
    rowCount: 1,
  });
  closeEditor();
}

function stageDelete(row: PointGridRow): void {
  if (!props.measurement) return;
  pendingOperations.value.push({
    id: `delete_${Date.now().toString(36)}_${row.__row}`,
    action: 'delete',
    label: 'Delete point',
    detail: identityLabel(row),
    statements: [buildMeasurementDeleteStatement(props.measurement, row)],
    rowCount: 1,
  });
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}

async function confirmPendingOperations(): Promise<void> {
  if (!props.measurement || pendingOperations.value.length === 0) return;
  writeBusy.value = true;
  const operations = [...pendingOperations.value];
  const statements = operations.flatMap((operation) => operation.statements);
  const isImport = operations.some((item) => item.action === 'import');
  importBusy.value = isImport;
  importCancelRequested.value = false;
  const started = performance.now();
  try {
    const batchSize = isImport ? 100 : Math.max(statements.length, 1);
    let affected = 0;
    importProgress.value = { done: 0, total: operations.reduce((sum, item) => sum + item.rowCount, 0) };
    for (let offset = 0; offset < statements.length; offset += batchSize) {
      if (importCancelRequested.value) throw new Error('已停止后续导入批次。');
      const chunk = statements.slice(offset, offset + batchSize);
      const results = await execDataSqlBatch(auth.api, props.targetDb, chunk);
      const failedIndex = results.findIndex((result) => result.error);
      if (failedIndex >= 0) {
        affected += isImport ? failedIndex : 0;
        importProgress.value = { done: Math.min(affected, importProgress.value.total), total: importProgress.value.total };
        throw new Error(results[failedIndex].error?.message ?? '批次写入失败。');
      }
      affected += isImport ? chunk.length : operations.reduce((sum, item) => sum + item.rowCount, 0);
      importProgress.value = { done: Math.min(affected, importProgress.value.total), total: importProgress.value.total };
    }
    recordOperation('success', isImport ? 'import' : 'edit', statements, affected, performance.now() - started, '');
    message.success(`已提交 ${affected} 个时序点。`);
    pendingOperations.value = [];
    importProgress.value = { done: affected, total: affected };
    await loadPoints();
  } catch (error) {
    const baseDetail = error instanceof Error ? error.message : '时序写入失败。';
    const remaining = isImport ? statements.slice(importProgress.value.done) : statements;
    const detail = isImport && importProgress.value.done > 0
      ? `${baseDetail} 已完成 ${importProgress.value.done} 点，保留 ${remaining.length} 点待重试。`
      : baseDetail;
    if (isImport) {
      pendingOperations.value = remaining.length > 0
        ? [{
          ...operations[0],
          id: `measurement_import_resume_${Date.now().toString(36)}`,
          detail: `${remaining.length} remaining points · resume import`,
          statements: remaining,
          rowCount: remaining.length,
        }]
        : [];
    }
    recordOperation('error', isImport ? 'import' : 'edit', statements, importProgress.value.done, performance.now() - started, detail);
    message.error(detail);
  } finally {
    writeBusy.value = false;
    importBusy.value = false;
  }
}

async function exportVisiblePoints(format: 'csv' | 'json'): Promise<void> {
  if (!props.measurement || !pointResult.value?.hasColumns) return;
  const rows = pointRows.value.map(({ __key: _key, __row: _row, ...row }) => row);
  const content = format === 'csv'
    ? buildCsv(rows, pointResult.value.columns)
    : buildJson(rows, pointResult.value.columns);
  const outcome = await saveTextFile(
    `${safeFileStem(`${props.targetDb}_${props.measurement.name}`, 'measurement')}.${format}`,
    content,
    format === 'csv' ? 'text/csv;charset=utf-8' : 'application/json;charset=utf-8',
  );
  if (outcome !== 'cancelled') message.success(`已导出 ${rows.length} 个时序点。`);
}

function identityLabel(point: Record<string, unknown>): string {
  return [
    `time=${formatSqlValue(point.time)}`,
    ...tagColumns.value.map((column) => `${column.name}=${formatSqlValue(point[column.name])}`),
  ].join(' · ');
}

function hasSamePointIdentity(left: Record<string, unknown>, right: Record<string, unknown>): boolean {
  return ['time', ...tagColumns.value.map((column) => column.name)]
    .every((name) => String(left[name] ?? '') === String(right[name] ?? ''));
}

function renderPointValue(value: unknown, column: ColumnInfo) {
  if (value === null || value === undefined) return h(NTag, { size: 'tiny', bordered: false }, { default: () => 'NULL' });
  if (columnRole(column) === 'time') {
    const number = Number(value);
    const text = Number.isFinite(number) ? new Date(number).toLocaleString() : String(value);
    return h('span', { class: 'measurement-time', title: String(value) }, text);
  }
  if (columnRole(column) === 'tag') return h(NTag, { size: 'tiny', type: 'info', bordered: false }, { default: () => String(value) });
  return h('code', { class: 'measurement-value' }, formatSqlValue(value));
}

function draftText(value: unknown): string { return value === null || value === undefined ? '' : String(value); }
function numberDraftValue(value: unknown): number | null { const number = Number(value); return Number.isFinite(number) ? number : null; }
function booleanDraftValue(value: unknown): string { return value === true || String(value).toLowerCase() === 'true' ? 'true' : 'false'; }

const importFormat = ref<MeasurementImportFormat>('csv');
const importText = ref('');
const fileInput = ref<HTMLInputElement | null>(null);
const importParsed = ref<ParsedImportData | null>(null);
const importMapping = ref<MeasurementImportMapping>({});
const importValidation = ref<MeasurementImportValidation | null>(null);
const importBusy = ref(false);
const importCancelRequested = ref(false);
const importProgress = ref({ done: 0, total: 0 });
const importProgressPercent = computed(() => importProgress.value.total ? Math.round(importProgress.value.done / importProgress.value.total * 100) : 0);
const importHeaderOptions = computed<SelectOption[]>(() => [
  { label: '跳过', value: '' },
  ...(importParsed.value?.headers ?? []).map((header) => ({ label: header, value: header })),
]);
const importPreviewRows = computed(() => (importParsed.value?.rows ?? []).slice(0, 8).map((row, index) => ({ __key: index, ...row })));
const importPreviewColumns = computed<DataTableColumns<Record<string, unknown>>>(() => (importParsed.value?.headers ?? []).slice(0, 10).map((header) => ({
  title: header,
  key: header,
  minWidth: 120,
  ellipsis: { tooltip: true },
  render: (row) => String(row[header] ?? ''),
})));
const importErrors = computed(() => (importValidation.value?.errors ?? importParsed.value?.errors ?? []).map((error, index) => ({ ...error, key: `${error.rowNumber}:${index}` })));
const importErrorColumns: DataTableColumns<ImportRowError & { key: string }> = [
  { title: '行', key: 'rowNumber', width: 80 },
  { title: '列', key: 'column', width: 160 },
  { title: '问题', key: 'message', minWidth: 280 },
];

async function onFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  importText.value = await file.text();
  const lower = file.name.toLowerCase();
  importFormat.value = lower.endsWith('.json') || lower.endsWith('.jsonl') || lower.endsWith('.ndjson') ? 'json' : 'csv';
  analyzeImport();
  input.value = '';
}

function analyzeImport(): void {
  if (!props.measurement) return;
  importParsed.value = parseMeasurementImport(importFormat.value, importText.value);
  importMapping.value = buildMeasurementImportMapping(props.measurement, importParsed.value.headers);
  importValidation.value = null;
  clearPendingOperations();
}

function updateImportMapping(column: string, source: string): void {
  importMapping.value = { ...importMapping.value, [column]: source };
  importValidation.value = null;
  clearPendingOperations();
}

function currentImportValidation(): MeasurementImportValidation | null {
  if (!props.measurement || !importParsed.value) return null;
  const validation = validateMeasurementImport(props.measurement, importParsed.value, importMapping.value);
  importValidation.value = validation;
  return validation;
}

function validateImportOnly(): void {
  const validation = currentImportValidation();
  if (!validation) return;
  if (validation.errors.length) message.error(`发现 ${validation.errors.length} 个导入问题。`);
  else message.success(`${validation.rows.length} 个时序点校验通过。`);
}

function stageImport(): void {
  if (!props.measurement) return;
  const validation = currentImportValidation();
  if (!validation || validation.errors.length > 0 || validation.rows.length === 0) {
    message.error(validation?.errors.length ? '请先修复导入问题。' : '没有可导入的数据点。');
    return;
  }
  const statements = buildMeasurementInsertStatements(props.measurement, validation.rows);
  importProgress.value = { done: 0, total: statements.length };
  pendingOperations.value = [{
    id: `measurement_import_${Date.now().toString(36)}`,
    action: 'import',
    label: 'Import measurement points',
    detail: `${statements.length} points · ${importFormat.value.toUpperCase()} · ${Object.values(importMapping.value).filter(Boolean).length} mapped columns`,
    statements,
    rowCount: statements.length,
  }];
}

function clearImport(): void {
  importText.value = '';
  importParsed.value = null;
  importValidation.value = null;
  importMapping.value = {};
  importProgress.value = { done: 0, total: 0 };
  clearPendingOperations();
}

const monitorModel = ref<MonitorModel>('measurement');
const monitorTarget = ref('');
const monitorInterval = ref(2000);
const monitorLimit = ref(100);
const monitorRunning = ref(false);
const monitorLoading = ref(false);
const monitorResult = ref<SqlResultSet | null>(null);
const monitorError = ref('');
const monitorUpdatedAt = ref(0);
let monitorTimer: number | null = null;
let monitorRequestId = 0;
const intervalOptions: SelectOption[] = [1000, 2000, 5000, 10000, 30000].map((value) => ({ label: `${value / 1000} 秒`, value }));
const monitorLimitOptions: SelectOption[] = [50, 100, 250, 500].map((value) => ({ label: `${value} 行`, value }));
const monitorTargetOptions = computed<SelectOption[]>(() => (monitorModel.value === 'measurement'
  ? props.measurements.map((item) => ({ label: item.name, value: item.name }))
  : props.tables.map((item) => ({ label: item.name, value: item.name }))));
const monitorRows = computed(() => monitorResult.value ? rowsToObjects<Record<string, unknown>>(monitorResult.value) : []);
const monitorGridRows = computed(() => monitorRows.value.map((row, index) => ({ __key: index, ...row })));
const monitorElapsedLabel = computed(() => monitorResult.value?.end ? `${monitorResult.value.end.elapsedMs.toFixed(2)} ms` : '—');
const monitorUpdatedLabel = computed(() => monitorUpdatedAt.value ? new Date(monitorUpdatedAt.value).toLocaleTimeString() : '—');
const monitorSql = computed(() => buildMonitorSql());
const monitorTableColumns = computed<DataTableColumns<Record<string, unknown>>>(() => (monitorResult.value?.columns ?? []).map((column) => ({
  title: column,
  key: column,
  minWidth: 130,
  ellipsis: { tooltip: true },
  render: (row) => formatSqlValue(row[column]),
})));

function buildMonitorSql(): string {
  if (!monitorTarget.value) return '';
  if (monitorModel.value === 'measurement') {
    return `SELECT * FROM ${formatSqlIdentifier(monitorTarget.value)} ORDER BY ${formatSqlIdentifier('time')} DESC LIMIT ${monitorLimit.value};`;
  }
  const table = props.tables.find((item) => item.name === monitorTarget.value);
  const order = table?.primaryKey[0] ? ` ORDER BY ${formatSqlIdentifier(table.primaryKey[0])} DESC` : '';
  return `SELECT * FROM ${formatSqlIdentifier(monitorTarget.value)}${order} LIMIT ${monitorLimit.value};`;
}

async function refreshMonitor(allowOverlap = false): Promise<void> {
  const sql = buildMonitorSql();
  if (!sql || (monitorLoading.value && !allowOverlap)) return;
  const requestId = ++monitorRequestId;
  monitorLoading.value = true;
  monitorError.value = '';
  try {
    const result = await execDataSql(auth.api, props.targetDb, sql);
    if (requestId !== monitorRequestId) return;
    monitorResult.value = result;
    monitorUpdatedAt.value = Date.now();
    if (result.error) monitorError.value = result.error.message;
  } catch (error) {
    monitorError.value = error instanceof Error ? error.message : '实时监控查询失败。';
  } finally {
    monitorLoading.value = false;
  }
}

function toggleMonitor(): void {
  monitorRunning.value = !monitorRunning.value;
  scheduleMonitor();
  if (monitorRunning.value) void refreshMonitor();
}

function scheduleMonitor(): void {
  if (monitorTimer !== null) window.clearInterval(monitorTimer);
  monitorTimer = null;
  if (monitorRunning.value) monitorTimer = window.setInterval(() => { void refreshMonitor(); }, monitorInterval.value);
}

const schemaTableColumns: DataTableColumns<ColumnInfo> = [
  { title: '列名', key: 'name', minWidth: 180 },
  { title: '角色', key: 'role', width: 130, render: (row) => h(NTag, { size: 'small', type: columnRole(row) === 'tag' ? 'info' : columnRole(row) === 'field' ? 'success' : 'warning', bordered: false }, { default: () => row.role }) },
  { title: '类型', key: 'dataType', minWidth: 180 },
  { title: '向量维度', key: 'vectorDimension', width: 120, render: (row) => row.vectorDimension ?? '—' },
  { title: '用途', key: '__purpose', minWidth: 280, render: (row) => columnRole(row) === 'time' ? '点时间戳与范围过滤' : columnRole(row) === 'tag' ? 'Series 身份、过滤与分组' : '采样值、聚合与趋势分析' },
];

function recordOperation(
  status: 'success' | 'error',
  action: string,
  statements: SqlStatementRequest[],
  affected: number,
  elapsedMs: number,
  error: string,
): void {
  history.record({
    kind: 'operation',
    status,
    title: `${props.measurement?.name ?? 'measurement'} ${action}`,
    target: props.measurement?.name ?? '',
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'measurement',
    action,
    command: statements.map((statement) => statement.sql).join('\n'),
    summary: error || `${affected} points`,
    recordsAffected: affected,
    elapsedMs,
  });
}

watch(() => props.measurement?.name, () => {
  pointResult.value = null;
  closeEditor();
  clearPendingOperations();
  if (props.measurement) {
    monitorTarget.value = props.measurement.name;
    void loadPoints();
  }
}, { immediate: true });

watch(monitorModel, () => {
  monitorTarget.value = monitorModel.value === 'measurement' ? props.measurements[0]?.name ?? '' : props.tables[0]?.name ?? '';
  monitorResult.value = null;
});
watch(monitorInterval, scheduleMonitor);
watch(monitorTarget, () => { if (monitorRunning.value) void refreshMonitor(true); });
watch(activeView, (view) => {
  if (view !== 'monitor' && monitorRunning.value) {
    monitorRunning.value = false;
    scheduleMonitor();
  }
});

onBeforeUnmount(() => {
  if (monitorTimer !== null) window.clearInterval(monitorTimer);
});
</script>

<style scoped>
.measurement-workbench { display: flex; flex: 1; flex-direction: column; min-width: 0; min-height: 0; background: #fff; }
.measurement-toolbar { display: flex; flex: 0 0 auto; align-items: center; justify-content: space-between; gap: 16px; min-height: 72px; padding: 11px 16px; border-bottom: 1px solid var(--sndb-border); }
.measurement-toolbar__identity { min-width: 0; }
.measurement-toolbar__title { font-size: 20px; font-weight: 650; }
.measurement-toolbar__meta { display: block; margin-top: 4px; font-size: 12px; }
.measurement-filterbar { display: grid; grid-template-columns: minmax(160px, 1fr) minmax(160px, 1fr) minmax(140px, .8fr) minmax(150px, .8fr) 100px auto; gap: 8px; align-items: end; padding: 10px 12px; border-bottom: 1px solid var(--sndb-border); background: #fbfcfd; }
.measurement-filterbar label, .point-field { display: grid; gap: 4px; min-width: 0; }
.measurement-filterbar label > span { color: var(--sndb-ink-muted); font-size: 11px; }
.measurement-filterbar input { width: 100%; height: 34px; padding: 0 9px; border: 1px solid var(--sndb-border-strong); border-radius: 5px; background: #fff; color: inherit; }
.measurement-alert { margin: 10px 12px 0; }
.point-editor { display: grid; gap: 10px; padding: 12px 14px; border-bottom: 1px solid var(--sndb-border); background: #f7faf8; }
.point-editor__head, .measurement-toolband, .measurement-import-actions, .measurement-schema > header, .monitor-chart-panel > header { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
.point-editor__title, .measurement-section-title { display: block; font-size: 14px; font-weight: 650; }
.point-editor__grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 10px; }
.point-field__label { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; font-size: 12px; font-weight: 600; }
.point-field__label small { color: var(--sndb-ink-muted); font-size: 10px; font-weight: 400; }
.point-field.is-time { grid-column: span 2; }
.measurement-grid-shell { flex: 1; min-height: 180px; overflow: hidden; }
.measurement-grid { height: 100%; }
.measurement-column-title { display: flex; flex-direction: column; align-items: flex-start; gap: 1px; line-height: 1.25; }
.measurement-column-title strong { font-size: 12px; font-weight: 600; }
.measurement-column-title small { display: block; color: var(--sndb-ink-muted); font-size: 10px; font-weight: 400; }
.measurement-time { font-variant-numeric: tabular-nums; }
.measurement-value { background: transparent; color: var(--sndb-ink-strong); }
.measurement-statusbar { display: flex; flex: 0 0 auto; align-items: center; justify-content: space-between; gap: 16px; min-height: 44px; padding: 7px 12px; border-top: 1px solid var(--sndb-border); color: var(--sndb-ink-muted); font-size: 12px; }
.measurement-statusbar code { max-width: 68%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.measurement-import, .measurement-monitor, .measurement-schema { flex: 1; min-height: 0; padding: 12px; overflow: auto; }
.measurement-import { display: flex; flex-direction: column; gap: 12px; }
.measurement-toolband, .measurement-import-actions { padding: 10px 12px; border: 1px solid var(--sndb-border); border-radius: 6px; background: #fbfcfd; }
.measurement-file-input { display: none; }
.measurement-import-grid { display: grid; grid-template-columns: minmax(260px, .75fr) minmax(0, 1.6fr); gap: 12px; min-height: 240px; }
.measurement-import-map, .measurement-import-preview { min-width: 0; border: 1px solid var(--sndb-border); border-radius: 6px; overflow: auto; }
.measurement-import-map > header, .measurement-import-preview > header { display: flex; justify-content: space-between; gap: 8px; padding: 9px 10px; border-bottom: 1px solid var(--sndb-border); }
.measurement-import-map > label { display: grid; grid-template-columns: minmax(130px, .8fr) minmax(140px, 1fr); gap: 8px; align-items: center; padding: 6px 10px; border-bottom: 1px solid #eef0f2; }
.measurement-import-map > label span { font-size: 12px; }
.measurement-import-map > label small { display: block; color: var(--sndb-ink-muted); font-size: 10px; }
.measurement-import-actions { align-items: center; }
.measurement-import-actions :deep(.n-progress) { width: min(460px, 50%); }
.monitor-controls { display: flex; align-items: center; gap: 8px; padding-bottom: 12px; border-bottom: 1px solid var(--sndb-border); }
.monitor-controls__target { width: min(340px, 30vw); }
.monitor-controls__interval { width: 110px; }
.monitor-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); border-bottom: 1px solid var(--sndb-border); background: #fbfcfd; }
.monitor-stats > div { display: grid; gap: 3px; padding: 12px 14px; border-right: 1px solid var(--sndb-border); }
.monitor-stats span { color: var(--sndb-ink-muted); font-size: 11px; }
.monitor-stats strong { font-size: 17px; font-weight: 600; }
.monitor-stats strong.is-live { color: var(--sndb-success); }
.monitor-chart-panel { min-height: 300px; padding: 14px 0; border-bottom: 1px solid var(--sndb-border); }
.monitor-chart-panel > header { align-items: center; margin-bottom: 12px; }
.monitor-chart-panel code { max-width: 56%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.monitor-grid-panel { height: 280px; min-height: 180px; padding-top: 12px; }
.measurement-schema > header { align-items: center; margin-bottom: 12px; padding-bottom: 12px; border-bottom: 1px solid var(--sndb-border); }
@media (max-width: 1100px) {
  .measurement-filterbar { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .measurement-import-grid { grid-template-columns: 1fr; }
  .monitor-controls { flex-wrap: wrap; }
  .monitor-controls__target { width: min(420px, 50vw); }
}
@media (max-width: 800px) {
  .measurement-toolbar { align-items: flex-start; }
  .measurement-filterbar { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .point-field.is-time { grid-column: span 1; }
  .monitor-stats { grid-template-columns: repeat(2, 1fr); }
  .monitor-chart-panel code { display: none; }
}
</style>
