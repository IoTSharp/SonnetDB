<template>
  <main
    class="measurement-workbench"
    data-testid="workbench-measurement"
    data-shell="five-zone"
    :data-state="measurementState"
    :data-database="targetDb"
    :data-resource-key="measurement?.name ?? ''"
  >
    <section class="measurement-toolbar" data-zone="toolbar">
      <div class="measurement-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="success" :bordered="false">MEASUREMENT</n-tag>
          <n-text class="measurement-toolbar__title">{{ measurement?.name ?? 'No measurement selected' }}</n-text>
          <n-tag v-if="tagColumns.length" size="tiny" :bordered="false">{{ tagColumns.length }} TAG</n-tag>
          <n-tag v-if="fieldColumns.length" size="tiny" :bordered="false">{{ fieldColumns.length }} FIELD</n-tag>
          <n-tag size="tiny" :bordered="false" data-testid="measurement-state">{{ stateDescriptor.label }}</n-tag>
        </n-space>
        <n-text depth="3" class="measurement-toolbar__meta">
          {{ targetDb || 'database' }} · 点级编辑、文件导入与目标级实时监控
        </n-text>
      </div>
      <n-space v-if="activeView === 'points'" size="small" align="center" :wrap="true">
        <n-button size="small" secondary :loading="loadingPoints" :disabled="permissionDenied" @click="loadPoints">刷新</n-button>
        <n-button size="small" quaternary :disabled="permissionDenied || pointRows.length === 0" @click="exportVisiblePoints('csv')">导出 CSV</n-button>
        <n-button size="small" quaternary :disabled="permissionDenied || pointRows.length === 0" @click="exportVisiblePoints('json')">导出 JSON</n-button>
        <n-button size="small" type="primary" :disabled="!measurement || readOnly || permissionDenied" @click="openNewPoint">新增数据点</n-button>
        <n-button size="small" quaternary @click="historyVisible = true">历史</n-button>
      </n-space>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeView"
      :items="sections"
      aria-label="时序数据工作区"
      data-zone="tabs"
      @update:model-value="activeView = $event as MeasurementView"
    />

    <section class="measurement-approval-zone" data-zone="approval">
      <WriteApprovalPanel
        v-if="approvalPlan && !readOnly && !permissionDenied"
        :plan="approvalPlan"
        :busy="writeBusy"
        :abortable="(writeBusy ? runningOperations : pendingOperations).some((item) => item.action === 'import')"
        @cancel="clearPendingOperations"
        @confirm="confirmPendingOperations"
        @abort="stopImport"
      />
    </section>

    <n-alert v-if="permissionDenied" type="warning" data-testid="measurement-permission-lock" class="measurement-alert">
      当前身份没有 Measurement 权限，点值、监控与写草稿已隐藏；刷新不会解除权限锁存。
    </n-alert>

    <!-- The parent workbench owns the five-zone shell. These anchors keep the
         center data plane and right Inspector context connectable without
         changing the legacy measurement route or resource key. -->
    <div class="measurement-zone-anchors" aria-hidden="true">
      <span data-zone="center" data-slot="measurement-data" />
      <span data-zone="context" data-slot="measurement-inspector" />
    </div>

    <template v-if="activeView === 'points'">
      <section class="measurement-filterbar" data-zone="center">
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
        <n-button size="small" secondary :loading="loadingPoints" :disabled="permissionDenied" @click="loadPoints">查询</n-button>
      </section>

      <n-alert v-if="errorMessage" type="error" :title="errorMessage" closable class="measurement-alert" @close="errorMessage = ''" />

      <section v-if="editorOpen && measurement && !readOnly && !permissionDenied" class="point-editor">
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
              :type="normalizedMeasurementType(column) === 'vector' ? 'textarea' : 'text'"
              :autosize="normalizedMeasurementType(column) === 'vector' ? { minRows: 2, maxRows: 5 } : undefined"
              size="small"
              :placeholder="normalizedMeasurementType(column) === 'vector' ? '[0.1, 0.2, 0.3]' : columnRole(column) === 'time' ? 'Unix ms / ISO 8601' : column.dataType"
              @update:value="pointDraft[column.name] = $event"
            />
          </label>
        </div>
        <n-alert v-if="pointValidationErrors.length" type="warning" :show-icon="false">
          {{ pointValidationErrors.map((item) => `${item.column ? `${item.column}: ` : ''}${item.message}`).join('；') }}
        </n-alert>
      </section>

      <section class="measurement-grid-shell">
        <n-empty v-if="permissionDenied" description="当前身份没有 Measurement 读取权限，点值已隐藏。" />
        <n-empty v-else-if="!measurement" description="请从资源浏览器选择 Measurement。" />
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
          virtual-scroll
          class="measurement-grid"
        />
      </section>
      <footer class="measurement-statusbar" data-zone="status">
        <span>{{ pointSummary }}</span>
        <code v-if="lastPointSql">{{ lastPointSql }}</code>
      </footer>
    </template>

    <section v-else-if="activeView === 'import'" class="measurement-import" data-zone="center">
      <section class="measurement-toolband">
        <div>
          <n-text class="measurement-section-title">Measurement 文件导入</n-text>
          <n-text depth="3">CSV / JSON / JSONL，自动映射列、类型校验、分批提交并记录操作历史。</n-text>
        </div>
        <n-space size="small" align="center" :wrap="true">
          <n-radio-group v-model:value="importFormat" size="small" :disabled="readOnly || permissionDenied">
            <n-radio-button value="csv">CSV</n-radio-button>
            <n-radio-button value="json">JSON / JSONL</n-radio-button>
          </n-radio-group>
          <input ref="fileInput" type="file" class="measurement-file-input" accept=".csv,.json,.jsonl,.ndjson,text/csv,application/json" @change="onFileSelected" />
          <n-button size="small" secondary :disabled="readOnly || permissionDenied" @click="fileInput?.click()">选择文件</n-button>
          <n-button size="small" type="primary" :disabled="readOnly || permissionDenied || !importText.trim()" @click="analyzeImport">解析</n-button>
          <n-button size="small" quaternary :disabled="readOnly || permissionDenied" @click="clearImport">清空</n-button>
        </n-space>
      </section>

      <n-input v-model:value="importText" type="textarea" :disabled="readOnly || permissionDenied" :autosize="{ minRows: 4, maxRows: 8 }" placeholder="粘贴 CSV、JSON 数组或 JSONL 数据" />

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
          <n-button size="small" secondary :disabled="permissionDenied || !importParsed" @click="validateImportOnly">校验</n-button>
          <n-button size="small" type="primary" :disabled="readOnly || permissionDenied || !importParsed || importBusy" @click="stageImport">暂存导入</n-button>
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

    <section v-else-if="activeView === 'monitor'" class="measurement-monitor" data-zone="center">
      <section class="monitor-controls">
        <n-radio-group v-model:value="monitorModel" size="small">
          <n-radio-button value="measurement">Measurement</n-radio-button>
          <n-radio-button value="table">关系表</n-radio-button>
        </n-radio-group>
        <n-select v-model:value="monitorTarget" size="small" :options="monitorTargetOptions" class="monitor-controls__target" />
        <n-select v-model:value="monitorInterval" size="small" :options="intervalOptions" class="monitor-controls__interval" />
        <n-select v-model:value="monitorLimit" size="small" :options="monitorLimitOptions" class="monitor-controls__interval" />
        <n-button size="small" secondary :loading="monitorLoading" :disabled="permissionDenied" @click="refreshMonitor(true)">立即刷新</n-button>
        <n-button size="small" :type="monitorRunning ? 'warning' : 'primary'" :disabled="permissionDenied" @click="toggleMonitor">
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
        <n-data-table :columns="monitorTableColumns" :data="monitorGridRows" :row-key="monitorRowKey" :loading="monitorLoading" :bordered="false" :pagination="false" size="small" flex-height virtual-scroll />
      </section>
    </section>

    <section v-else class="measurement-schema" data-zone="center">
      <header>
        <div>
          <n-text class="measurement-section-title">Measurement Schema</n-text>
          <n-text depth="3">时序身份由 time + TAG 确定，FIELD 存放可采样值。</n-text>
        </div>
        <n-button size="small" secondary :disabled="permissionDenied" @click="openSchemaSql">在 SQL 中查看</n-button>
      </header>
      <n-empty v-if="permissionDenied" description="当前身份没有 Measurement Schema 权限，结构载荷已隐藏。" />
      <n-data-table v-else :columns="schemaTableColumns" :data="columns" :bordered="false" :pagination="false" size="small" />
    </section>

    <WorkbenchHistoryDrawer v-model:show="historyVisible" :active-database="targetDb" />
  </main>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, reactive, ref, watch } from 'vue';
import type { AxiosInstance } from 'axios';
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
  /** 只读宿主仍可查询、导出和查看 Schema，但不能暂存或提交写入。 */
  readOnly?: boolean;
  /** 服务端明确拒绝 Measurement 载荷时隐藏点值与 Schema。 */
  permissionDenied?: boolean;
  /** 父宿主渲染代际，仅用于隔离当前子页权限通知。 */
  permissionGeneration?: number;
}>(), {
  measurements: () => [],
  tables: () => [],
  loading: false,
  readOnly: false,
  permissionDenied: false,
  permissionGeneration: 0,
});

const emit = defineEmits<{
  openSql: [sql: string];
  refreshSchema: [];
  permissionRejected: [denial: MeasurementPermissionDenial];
}>();

interface MeasurementPermissionDenial {
  database: string;
  measurement: string;
  generation: number;
}

type MeasurementView = 'points' | 'import' | 'monitor' | 'schema';
type MonitorModel = 'measurement' | 'table';
type MeasurementWorkbenchState = 'normal' | 'empty' | 'error' | 'permission' | 'readonly' | 'longContent';
interface MeasurementStateDescriptor {
  label: string;
  primary: string;
  summary: string;
}

/**
 * 生产 Measurement 工作台的六态合同；具体载荷仍由服务端结果决定。
 * 该表供外壳/Inspector 接线和回归测试读取，不能把静态状态当作服务端证据。
 */
const measurementStateContract: Record<MeasurementWorkbenchState, MeasurementStateDescriptor> = {
  normal: { label: '正常', primary: '查询数据点', summary: '显示当前 Measurement 的时间窗、TAG/FIELD 与有界点结果。' },
  empty: { label: '空', primary: '调整时间范围', summary: '没有可显示点时保留 Measurement、时间窗和 TAG 过滤。' },
  error: { label: '错误', primary: '检查并重试', summary: '只替换点结果并保留当前查询输入。' },
  permission: { label: '无权限', primary: '查看数据库权限', summary: '隐藏点值与 Schema 载荷，说明数据库 Measurement Read 权限。' },
  readonly: { label: '只读', primary: '导出当前结果', summary: '查询、刷新、导出和 Schema 可用，写入/删除/导入提交禁用。' },
  longContent: { label: '长结果', primary: '查看当前预览', summary: '当前窗口最多 500 行，不代表全部点或服务端资源预算。' },
};
interface PointGridRow extends Record<string, unknown> { __key: string; __row: number }
interface PendingOperation {
  id: string;
  action: 'insert' | 'replace' | 'delete' | 'import';
  label: string;
  detail: string;
  statements: SqlStatementRequest[];
  rowCount: number;
  context: MeasurementContext;
}

interface MeasurementContext {
  epoch: number;
  permissionGeneration: number;
  database: string;
  measurement: string;
  connectionId: string;
  connectionName: string;
  endpoint: string;
  profileEndpoint: string;
  token: string;
  schema: string;
  api: AxiosInstance;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();
const permissionLocked = ref(false);
const permissionDenied = computed(() => props.permissionDenied || permissionLocked.value);
const readOnly = computed(() => props.readOnly);
const canWrite = computed(() => !disposed && !readOnly.value && !permissionDenied.value && Boolean(props.targetDb && props.measurement));
let disposed = false;
let contextEpoch = 0;
let lastValidIdentity = '';
let pointController: AbortController | null = null;
let monitorController: AbortController | null = null;
let writeController: AbortController | null = null;
let fileRequestId = 0;
let writeRequestId = 0;
const PreviewMaxRows = 500;
const MaxWriteStatements = 1000;
const activeView = ref<MeasurementView>('points');
const sections: WorkbenchSectionTab[] = [
  { key: 'points', label: '数据点' },
  { key: 'import', label: '文件导入' },
  { key: 'monitor', label: '实时监控' },
  { key: 'schema', label: 'Schema' },
];

const columns = computed(() => permissionDenied.value ? [] : measurementColumns(props.measurement));
const tagColumns = computed(() => columns.value.filter((column) => columnRole(column) === 'tag'));
const fieldColumns = computed(() => columns.value.filter((column) => columnRole(column) === 'field'));
const pointResult = ref<SqlResultSet | null>(null);
const loadingPoints = ref(false);
let pointRequestId = 0;
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
const runningOperations = ref<PendingOperation[]>([]);
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
const measurementState = computed<MeasurementWorkbenchState>(() => {
  if (permissionDenied.value) return 'permission';
  if (readOnly.value) return 'readonly';
  if (!props.measurement) return 'empty';
  if (pointResult.value?.error || errorMessage.value || monitorError.value) return 'error';
  if (pointResult.value?.end && pointRows.value.length === 0) return 'empty';
  if (pointResult.value?.end?.truncated || pointRows.value.length >= previewLimit(pointLimit.value)) return 'longContent';
  return 'normal';
});
const stateDescriptor = computed(() => measurementStateContract[measurementState.value]);

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
      h(NButton, { size: 'tiny', secondary: true, disabled: readOnly.value || permissionDenied.value, onClick: () => openEditPoint(row) }, { default: () => '校正' }),
      h(NButton, { size: 'tiny', tertiary: true, type: 'error', disabled: readOnly.value || permissionDenied.value, onClick: () => stageDelete(row) }, { default: () => '删除' }),
    ] }),
  },
]);

const approvalPlan = computed<WriteApprovalPlan | null>(() => {
  const operations = writeBusy.value ? runningOperations.value : pendingOperations.value;
  if (!canWrite.value || operations.length === 0 || !operations.every((operation) => isCurrentContext(operation.context))) return null;
  const context = operations[0]!.context;
  return createWriteApprovalPlan({
    id: `measurement_${context.database}_${context.measurement}_${operations.map((item) => item.id).join('_')}`,
    title: operations.some((item) => item.action === 'import') ? 'Measurement import' : 'Measurement point changes',
    target: `${context.database}.${context.measurement}`,
    items: operations.map((operation) => ({
      id: operation.id,
      command: operation.action === 'import'
        ? `INSERT ${operation.rowCount} POINTS INTO ${formatSqlIdentifier(context.measurement)}`
        : operation.statements.map((statement) => statement.sql).join('\n'),
      severity: operation.action === 'delete' || operation.action === 'replace' ? 'danger' : 'write',
      label: operation.label,
      detail: operation.detail,
    })),
  });
});

async function loadPoints(): Promise<void> {
  if (disposed || !props.measurement || !props.targetDb || permissionDenied.value) return;
  const context = captureContext();
  const requestId = ++pointRequestId;
  pointController?.abort();
  const controller = pointController = new AbortController();
  const limit = previewLimit(pointLimit.value);
  loadingPoints.value = true;
  errorMessage.value = '';
  const parameters: SqlParameters = { limit: sqlParameterFromValue(limit) };
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
    `FROM ${formatSqlIdentifier(context.measurement)}`,
    predicates.length ? `WHERE ${predicates.join(' AND ')}` : '',
    `ORDER BY ${formatSqlIdentifier('time')} DESC`,
    'LIMIT @limit;',
  ].filter(Boolean).join('\n');
  lastPointSql.value = sql;
  try {
    const result = await execDataSql(context.api, context.database, sql, parameters, controller.signal, limit);
    if (!isCurrentContext(context) || requestId !== pointRequestId || controller.signal.aborted || permissionDenied.value) return;
    if (isPermissionFailure(result.error)) { lockPermission(context); return; }
    pointResult.value = boundedReadResult(result, limit);
    if (pointResult.value.error) errorMessage.value = pointResult.value.error.message;
    recordRead(context, context.measurement, sql, pointResult.value, 'points');
  } catch (error) {
    if (!isCurrentContext(context) || requestId !== pointRequestId || controller.signal.aborted) return;
    if (isPermissionFailure(error)) { lockPermission(context); return; }
    pointResult.value = null;
    errorMessage.value = '加载数据点失败，请检查连接后重试。';
  } finally {
    if (pointController === controller) pointController = null;
    if (!disposed && requestId === pointRequestId) loadingPoints.value = false;
  }
}

function openNewPoint(): void {
  if (!canWrite.value || writeBusy.value) return;
  resetPointDraft();
  pointDraft.time = new Date().toISOString();
  editingSource.value = null;
  editorOpen.value = true;
}

function openEditPoint(row: PointGridRow): void {
  if (!canWrite.value || writeBusy.value) return;
  resetPointDraft();
  for (const column of columns.value) {
    pointDraft[column.name] = row[column.name];
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
  if (!props.measurement || !canWrite.value || writeBusy.value) return;
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
  enqueueOperation({
    id: `point_${Date.now().toString(36)}`,
    action: replacing ? 'replace' : 'insert',
    label: replacing ? 'Correct point identity' : 'Insert point',
    detail: identityLabel(validation.values),
    statements,
    rowCount: 1,
    context: captureContext(),
  });
  closeEditor();
}

function stageDelete(row: PointGridRow): void {
  if (!props.measurement || !canWrite.value || writeBusy.value) return;
  enqueueOperation({
    id: `delete_${Date.now().toString(36)}_${row.__row}`,
    action: 'delete',
    label: 'Delete point',
    detail: identityLabel(row),
    statements: [buildMeasurementDeleteStatement(props.measurement, row)],
    rowCount: 1,
    context: captureContext(),
  });
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}

async function confirmPendingOperations(): Promise<void> {
  if (!canWrite.value || writeBusy.value || pendingOperations.value.length === 0) return;
  const operations = [...pendingOperations.value];
  const context = operations[0]!.context;
  if (!operations.every((operation) => isCurrentContext(operation.context))) { clearPendingOperations(); return; }
  const statements = operations.flatMap((operation) => operation.statements.map((statement) => ({
    ...statement,
    parameters: statement.parameters ? Object.fromEntries(Object.entries(statement.parameters).map(([key, value]) => [key, { ...value }])) : undefined,
  })));
  if (statements.length > MaxWriteStatements) { clearPendingOperations(); message.error('每次审批最多 1000 条时序语句。'); return; }
  const requestId = ++writeRequestId;
  const controller = writeController = new AbortController();
  // 消费原审批；仅主动停止且服务端已确认的导入批次可以生成全新的剩余审批。
  pendingOperations.value = [];
  runningOperations.value = operations;
  writeBusy.value = true;
  const isImport = operations.some((item) => item.action === 'import');
  importBusy.value = isImport;
  importCancelRequested.value = false;
  const started = performance.now();
  const deadline = Date.now() + 60_000;
  const timeout = setTimeout(() => controller.abort(), 60_000);
  let affected = 0;
  let completedStatements = 0;
  let dispatched = false;
  let status: 'success' | 'error' | 'unknown' = 'success';
  let detail = '';
  let stoppedByUser = false;
  try {
    const batchSize = isImport ? 100 : Math.max(statements.length, 1);
    importProgress.value = { done: 0, total: operations.reduce((sum, item) => sum + item.rowCount, 0) };
    for (let offset = 0, batch = 0; offset < statements.length && batch < 10 && Date.now() < deadline; offset += batchSize, batch += 1) {
      if (!isCurrentContext(context) || !canWrite.value || controller.signal.aborted) { status = 'unknown'; break; }
      if (importCancelRequested.value) { stoppedByUser = true; status = 'error'; break; }
      const chunk = statements.slice(offset, offset + batchSize);
      dispatched = true;
      const results = await execDataSqlBatch(context.api, context.database, chunk, controller.signal);
      const failedIndex = results.findIndex((result) => result.error);
      if (failedIndex >= 0) {
        const failure = results[failedIndex]!.error;
        const current = isCurrentContext(context) && requestId === writeRequestId && !controller.signal.aborted;
        if (current && isPermissionFailure(failure)) lockPermission(context);
        affected += isImport ? failedIndex : 0;
        status = current ? isUncertainSqlFailure(failure) ? 'unknown' : 'error' : 'unknown';
        detail = isPermissionFailure(failure) ? '时序写入被权限拒绝。' : status === 'unknown' ? '写入结果未知，请核对服务端状态；未自动重试。' : '时序写入被服务端拒绝。';
        break;
      }
      if (results.length !== chunk.length || results.some((result) => !result.end)) { status = 'unknown'; break; }
      completedStatements += chunk.length;
      affected += isImport ? chunk.length : operations.reduce((sum, item) => sum + item.rowCount, 0);
      if (!isCurrentContext(context) || !canWrite.value || controller.signal.aborted) { status = 'unknown'; break; }
      importProgress.value = { done: Math.min(affected, importProgress.value.total), total: importProgress.value.total };
    }
    if (completedStatements < statements.length && status === 'success') status = 'unknown';
  } catch (error) {
    const current = isCurrentContext(context) && requestId === writeRequestId && !controller.signal.aborted;
    if (current && isPermissionFailure(error)) lockPermission(context);
    const httpStatus = (error as { response?: { status?: number } } | null)?.response?.status;
    status = current && typeof httpStatus === 'number' && httpStatus >= 400 && httpStatus < 500 && httpStatus !== 408 ? 'error' : 'unknown';
    detail = status === 'error' ? '时序写入被服务端拒绝。' : '写入结果未知，请核对服务端状态；未自动重试。';
  } finally {
    clearTimeout(timeout);
    if (writeController === controller) writeController = null;
    const current = isCurrentContext(context) && requestId === writeRequestId && canWrite.value;
    if (!current && status === 'success') status = 'unknown';
    if (status === 'unknown' && !detail) detail = '写入结果未知，请核对服务端状态；未自动重试。客户端取消不代表服务端回滚。';
    if (current && stoppedByUser && isImport) {
      const remaining = statements.slice(completedStatements);
      detail = `已停止后续导入批次。已完成 ${affected} 点，剩余 ${remaining.length} 点需要重新审批。`;
      pendingOperations.value = remaining.length ? [{ ...operations[0]!, context,
        id: `measurement_import_resume_${Date.now().toString(36)}`,
        detail: `${remaining.length} remaining points · resume import`, statements: remaining, rowCount: remaining.length }] : [];
    }
    if (dispatched) recordOperation(context, status, isImport ? 'import' : 'edit', statements, affected, performance.now() - started, detail);
    if (!disposed && requestId === writeRequestId) {
      writeBusy.value = false;
      importBusy.value = false;
      runningOperations.value = [];
      if (current) {
        if (status === 'success') {
          importProgress.value = { done: affected, total: affected };
          message.success(`已提交 ${affected} 个时序点。`);
          void loadPoints();
        } else message.error(detail);
      }
    }
  }
}

function stopImport(): void {
  if (!disposed && writeBusy.value && runningOperations.value.some((operation) => operation.action === 'import')) importCancelRequested.value = true;
}

function enqueueOperation(operation: PendingOperation): void {
  if (!canWrite.value || writeBusy.value || !isCurrentContext(operation.context)) return;
  if (pendingOperations.value.reduce((count, item) => count + item.statements.length, 0) + operation.statements.length > MaxWriteStatements) {
    message.error('每次审批最多 1000 条时序语句。'); return;
  }
  pendingOperations.value.push(operation);
}

async function exportVisiblePoints(format: 'csv' | 'json'): Promise<void> {
  if (disposed || !props.measurement || permissionDenied.value || !pointResult.value?.hasColumns) return;
  const context = captureContext();
  const rows = pointRows.value.map(({ __key: _key, __row: _row, ...row }) => row);
  const content = format === 'csv'
    ? buildCsv(rows, pointResult.value.columns)
    : buildJson(rows, pointResult.value.columns);
  const outcome = await saveTextFile(
    `${safeFileStem(`${props.targetDb}_${props.measurement.name}`, 'measurement')}.${format}`,
    content,
    format === 'csv' ? 'text/csv;charset=utf-8' : 'application/json;charset=utf-8',
  );
  if (isCurrentContext(context) && !permissionDenied.value && outcome !== 'cancelled') message.success(`已导出 ${rows.length} 个时序点。`);
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
  if (!canWrite.value || writeBusy.value) { input.value = ''; return; }
  const file = input.files?.[0];
  if (!file) return;
  const context = captureContext();
  const requestId = ++fileRequestId;
  try {
    const text = await file.text();
    if (!isCurrentContext(context) || requestId !== fileRequestId || !canWrite.value || writeBusy.value) return;
    importText.value = text;
    const lower = file.name.toLowerCase();
    importFormat.value = lower.endsWith('.json') || lower.endsWith('.jsonl') || lower.endsWith('.ndjson') ? 'json' : 'csv';
    analyzeImport();
  } catch {
    if (isCurrentContext(context) && requestId === fileRequestId && canWrite.value) message.error('读取导入文件失败。');
  } finally { input.value = ''; }
}

function analyzeImport(): void {
  if (!props.measurement || !canWrite.value || writeBusy.value) return;
  importParsed.value = parseMeasurementImport(importFormat.value, importText.value);
  importMapping.value = buildMeasurementImportMapping(props.measurement, importParsed.value.headers);
  importValidation.value = null;
  clearPendingOperations();
}

function updateImportMapping(column: string, source: string): void {
  if (!canWrite.value || writeBusy.value) return;
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
  if (disposed || permissionDenied.value) return;
  const validation = currentImportValidation();
  if (!validation) return;
  if (validation.errors.length) message.error(`发现 ${validation.errors.length} 个导入问题。`);
  else message.success(`${validation.rows.length} 个时序点校验通过。`);
}

function stageImport(): void {
  if (!props.measurement || !canWrite.value || writeBusy.value) return;
  if ((importParsed.value?.rows.length ?? 0) > MaxWriteStatements) { message.error('每次审批导入最多 1000 个时序点。'); return; }
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
    context: captureContext(),
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
let monitorDeadlineTimer: number | null = null;
let monitorRequestId = 0;
let monitorGeneration = 0;
let autoMonitorId = 0;
let monitorRounds = 0;
let monitorDeadline = 0;
const intervalOptions: SelectOption[] = [1000, 2000, 5000, 10000, 30000].map((value) => ({ label: `${value / 1000} 秒`, value }));
const monitorLimitOptions: SelectOption[] = [50, 100, 250, 500].map((value) => ({ label: `${value} 行`, value }));
const monitorTargetOptions = computed<SelectOption[]>(() => permissionDenied.value ? [] : (monitorModel.value === 'measurement'
  ? props.measurements.map((item) => ({ label: item.name, value: item.name }))
  : props.tables.map((item) => ({ label: item.name, value: item.name }))));
const monitorRows = computed(() => monitorResult.value ? rowsToObjects<Record<string, unknown>>(monitorResult.value) : []);
const monitorRowId = Symbol('monitorRowId');
const monitorGridRows = computed(() => monitorRows.value.map((row, index) => ({ ...row, [monitorRowId]: index })));
function monitorRowKey(row: Record<typeof monitorRowId, number>): number { return row[monitorRowId]; }
const monitorElapsedLabel = computed(() => monitorResult.value?.end ? `${monitorResult.value.end.elapsedMs.toFixed(2)} ms` : '—');
const monitorUpdatedLabel = computed(() => monitorUpdatedAt.value ? new Date(monitorUpdatedAt.value).toLocaleTimeString() : '—');
const monitorSql = computed(() => permissionDenied.value ? '' : buildMonitorSql());
const monitorTableColumns = computed<DataTableColumns<Record<string, unknown>>>(() => (monitorResult.value?.columns ?? []).map((column) => ({
  title: column,
  key: column,
  minWidth: 130,
  ellipsis: { tooltip: true },
  render: (row) => formatSqlValue(row[column]),
})));

function buildMonitorSql(): string {
  if (!monitorTarget.value) return '';
  const limit = previewLimit(monitorLimit.value);
  if (monitorModel.value === 'measurement') {
    return `SELECT * FROM ${formatSqlIdentifier(monitorTarget.value)} ORDER BY ${formatSqlIdentifier('time')} DESC LIMIT ${limit};`;
  }
  const table = props.tables.find((item) => item.name === monitorTarget.value);
  const order = table?.primaryKey[0] ? ` ORDER BY ${formatSqlIdentifier(table.primaryKey[0])} DESC` : '';
  return `SELECT * FROM ${formatSqlIdentifier(monitorTarget.value)}${order} LIMIT ${limit};`;
}

async function refreshMonitor(allowOverlap = false): Promise<void> {
  if (disposed || !props.targetDb || permissionDenied.value) return;
  const sql = buildMonitorSql();
  if (!sql || (monitorLoading.value && !allowOverlap)) return;
  const context = captureContext();
  const target = monitorTarget.value;
  const generation = monitorGeneration;
  const limit = previewLimit(monitorLimit.value);
  const requestId = ++monitorRequestId;
  monitorController?.abort();
  const controller = monitorController = new AbortController();
  monitorLoading.value = true;
  monitorError.value = '';
  try {
    const result = await execDataSql(context.api, context.database, sql, undefined, controller.signal, limit);
    if (!isCurrentContext(context) || requestId !== monitorRequestId || generation !== monitorGeneration || controller.signal.aborted || permissionDenied.value) return;
    if (isPermissionFailure(result.error)) { lockPermission(context); return; }
    monitorResult.value = boundedReadResult(result, limit);
    monitorUpdatedAt.value = Date.now();
    if (monitorResult.value.error) monitorError.value = monitorResult.value.error.message;
    recordRead(context, target, sql, monitorResult.value, 'monitor');
  } catch (error) {
    if (!isCurrentContext(context) || requestId !== monitorRequestId || generation !== monitorGeneration || controller.signal.aborted) return;
    if (isPermissionFailure(error)) { lockPermission(context); return; }
    monitorResult.value = null;
    monitorUpdatedAt.value = 0;
    monitorError.value = '实时监控查询失败，请检查连接后重试。';
  } finally {
    if (monitorController === controller) monitorController = null;
    if (!disposed && requestId === monitorRequestId) monitorLoading.value = false;
  }
}

function toggleMonitor(): void {
  if (disposed || permissionDenied.value || !props.targetDb || !monitorTarget.value) return;
  if (monitorRunning.value) { stopMonitor(); return; }
  monitorRunning.value = true;
  monitorRounds = 0;
  monitorDeadline = Date.now() + 60_000;
  const runId = ++autoMonitorId;
  monitorDeadlineTimer = window.setTimeout(() => { if (runId === autoMonitorId) stopMonitor(); }, 60_000);
  void runMonitorRound(runId);
}

function scheduleMonitor(): void {
  if (monitorTimer !== null) window.clearTimeout(monitorTimer);
  monitorTimer = null;
  if (!monitorRunning.value) return;
  if (monitorRounds >= 12 || Date.now() >= monitorDeadline) { stopMonitor(false); return; }
  const runId = autoMonitorId;
  const interval = Math.min(30_000, Math.max(1000, Number.isFinite(monitorInterval.value) ? monitorInterval.value : 2000));
  monitorTimer = window.setTimeout(() => { monitorTimer = null; void runMonitorRound(runId); }, Math.min(interval, monitorDeadline - Date.now()));
}

async function runMonitorRound(runId: number): Promise<void> {
  if (disposed || !monitorRunning.value || runId !== autoMonitorId) return;
  if (monitorRounds >= 12 || Date.now() >= monitorDeadline) { stopMonitor(); return; }
  monitorRounds += 1;
  await refreshMonitor();
  if (!disposed && runId === autoMonitorId && monitorRunning.value) scheduleMonitor();
}

function stopMonitor(cancelRequest = true): void {
  autoMonitorId += 1;
  monitorRunning.value = false;
  if (monitorTimer !== null) window.clearTimeout(monitorTimer);
  if (monitorDeadlineTimer !== null) window.clearTimeout(monitorDeadlineTimer);
  monitorTimer = null;
  monitorDeadlineTimer = null;
  if (cancelRequest) {
    monitorRequestId += 1;
    monitorController?.abort();
    monitorController = null;
    monitorLoading.value = false;
  }
}

const schemaTableColumns: DataTableColumns<ColumnInfo> = [
  { title: '列名', key: 'name', minWidth: 180 },
  { title: '角色', key: 'role', width: 130, render: (row) => h(NTag, { size: 'small', type: columnRole(row) === 'tag' ? 'info' : columnRole(row) === 'field' ? 'success' : 'warning', bordered: false }, { default: () => row.role }) },
  { title: '类型', key: 'dataType', minWidth: 180 },
  { title: '向量维度', key: 'vectorDimension', width: 120, render: (row) => row.vectorDimension ?? '—' },
  { title: '用途', key: '__purpose', minWidth: 280, render: (row) => columnRole(row) === 'time' ? '点时间戳与范围过滤' : columnRole(row) === 'tag' ? 'Series 身份、过滤与分组' : '采样值、聚合与趋势分析' },
];

function recordOperation(
  context: MeasurementContext,
  status: 'success' | 'error' | 'unknown',
  action: string,
  statements: SqlStatementRequest[],
  affected: number,
  elapsedMs: number,
  error: string,
): void {
  history.record({
    kind: 'operation',
    status,
    title: `${context.measurement || 'measurement'} ${action}`,
    target: context.measurement,
    database: context.database,
    connectionId: context.connectionId,
    connectionName: context.connectionName,
    model: 'measurement',
    action,
    command: statements.map((statement) => statement.sql).join('\n'),
    summary: error || `${affected} points`,
    recordsAffected: affected,
    elapsedMs,
    completeness: status === 'unknown' ? 'unknown' : status === 'error' ? 'partial' : 'complete',
  });
}

function captureContext(): MeasurementContext {
  return {
    epoch: contextEpoch, permissionGeneration: props.permissionGeneration,
    database: props.targetDb, measurement: props.measurement?.name ?? '',
    connectionId: connections.activeProfileId, connectionName: connections.activeProfile.name,
    endpoint: auth.api.defaults.baseURL ?? '/', profileEndpoint: connections.activeBaseUrl,
    token: auth.state?.token ?? '', schema: JSON.stringify([props.measurement, props.measurements, props.tables]), api: auth.api,
  };
}

function isCurrentContext(context: MeasurementContext): boolean {
  if (disposed || context.epoch !== contextEpoch) return false;
  const current = captureContext();
  return context.permissionGeneration === current.permissionGeneration
    && context.database === current.database && context.measurement === current.measurement
    && context.connectionId === current.connectionId && context.endpoint === current.endpoint
    && context.profileEndpoint === current.profileEndpoint && context.token === current.token
    && context.schema === current.schema && context.api === current.api;
}

function isPermissionFailure(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const candidate = error as { code?: unknown; response?: { status?: unknown; data?: { code?: unknown } } };
  return candidate.response?.status === 401 || candidate.response?.status === 403
    || ['forbidden', 'unauthorized', 'permission_denied', 'access_denied', 'http_401', 'http_403'].includes(String(candidate.code ?? candidate.response?.data?.code ?? ''));
}

function isUncertainSqlFailure(error: SqlResultSet['error']): boolean {
  const code = error?.code ?? '';
  return ['invalid_sql_response', 'incomplete_sql_response', 'http_408'].includes(code) || /^http_5[0-9]{2}$/.test(code);
}

function previewLimit(value: number): number {
  return Math.min(PreviewMaxRows, Math.max(1, Number.isFinite(value) ? Math.trunc(value) : 100));
}

function boundedReadResult(result: SqlResultSet, limit: number): SqlResultSet {
  const error = result.error ? { code: result.error.code, message: '时序查询被服务端拒绝或响应无效，请检查后重试。' }
    : !result.end ? { code: 'incomplete_sql_response', message: '读取缺少完成标记，请检查后重试。' } : null;
  const rows = error ? [] : result.rows.slice(0, limit);
  return { ...result, rows, error, end: result.end ? { ...result.end, rowCount: rows.length,
    truncated: result.end.truncated === true || result.rows.length > rows.length } : null };
}

function recordRead(context: MeasurementContext, target: string, sql: string, result: SqlResultSet, action: string): void {
  history.record({ kind: 'query', status: result.error ? 'error' : 'success', title: `${target} ${action}`,
    target, database: context.database, connectionId: context.connectionId, connectionName: context.connectionName,
    model: 'measurement', action, command: sql, summary: result.error?.message ?? `${result.rows.length} preview rows`,
    rowCount: result.rows.length, recordsAffected: -1, elapsedMs: result.end?.elapsedMs ?? 0,
    completeness: result.error ? 'partial' : result.end?.truncated ? 'truncated' : 'complete' });
}

function invalidateContext(clearReads = true): void {
  contextEpoch += 1;
  pointRequestId += 1;
  monitorRequestId += 1;
  fileRequestId += 1;
  writeRequestId += 1;
  pointController?.abort(); monitorController?.abort(); writeController?.abort();
  pointController = null; monitorController = null; writeController = null;
  loadingPoints.value = false; monitorLoading.value = false; writeBusy.value = false; importBusy.value = false;
  stopMonitor();
  if (clearReads) { pointResult.value = null; monitorResult.value = null; monitorUpdatedAt.value = 0; lastPointSql.value = ''; }
  errorMessage.value = '';
  monitorError.value = '';
  closeEditor();
  resetPointDraft();
  runningOperations.value = [];
  clearImport();
}

function lockPermission(context?: MeasurementContext): void {
  if (context && (!isCurrentContext(context) || permissionDenied.value)) return;
  const denial: MeasurementPermissionDenial | null = context && context.database && context.measurement ? {
    database: context.database, measurement: context.measurement, generation: context.permissionGeneration,
  } : null;
  permissionLocked.value = true;
  invalidateContext();
  errorMessage.value = '当前身份没有 Measurement 权限。';
  if (denial) emit('permissionRejected', denial);
}

function openSchemaSql(): void {
  if (disposed || permissionDenied.value || !props.measurement) return;
  emit('openSql', `DESCRIBE MEASUREMENT ${formatSqlIdentifier(props.measurement.name)}`);
}

const identity = computed(() => [props.targetDb, props.measurement?.name ?? '', connections.activeProfileId,
  connections.activeBaseUrl, auth.api.defaults.baseURL ?? '/'].join('\u001f'));

watch(identity, (value) => {
  const valid = Boolean(props.targetDb && props.measurement?.name && connections.activeProfileId && connections.activeBaseUrl && auth.api.defaults.baseURL);
  if (valid && value !== lastValidIdentity) { permissionLocked.value = false; lastValidIdentity = value; }
  invalidateContext();
  if (props.measurement) {
    monitorTarget.value = props.measurement.name;
    void loadPoints();
  }
}, { immediate: true, flush: 'sync' });

watch([() => auth.state, () => auth.state?.token, () => auth.api,
  () => props.measurement, () => props.measurements, () => props.tables,
  () => JSON.stringify([props.measurement, props.measurements, props.tables])], () => {
  invalidateContext();
  if (!permissionDenied.value) void loadPoints();
}, { flush: 'sync' });

watch(readOnly, () => { invalidateContext(false); }, { flush: 'sync' });
watch(() => props.permissionGeneration, () => { invalidateContext(); }, { flush: 'sync' });
watch(() => props.permissionDenied, (denied) => {
  if (denied) lockPermission();
}, { immediate: true, flush: 'sync' });

watch([monitorModel, monitorTarget, monitorLimit], () => {
  monitorGeneration += 1;
  monitorRequestId += 1;
  monitorController?.abort();
  monitorController = null;
  monitorLoading.value = false;
  monitorResult.value = null;
  monitorUpdatedAt.value = 0;
  monitorError.value = '';
  if (monitorRunning.value) void refreshMonitor(true);
}, { flush: 'sync' });

watch(monitorModel, () => {
  monitorTarget.value = monitorModel.value === 'measurement' ? props.measurements[0]?.name ?? '' : props.tables[0]?.name ?? '';
}, { flush: 'sync' });
watch(monitorInterval, scheduleMonitor, { flush: 'sync' });
watch(activeView, (view) => {
  if (view !== 'monitor') stopMonitor();
}, { flush: 'sync' });

onBeforeUnmount(() => {
  disposed = true;
  invalidateContext();
});
</script>

<style scoped>
.measurement-workbench { display: flex; flex: 1; flex-direction: column; min-width: 0; min-height: 0; background: #fff; }
.measurement-zone-anchors { display: none; }
.measurement-approval-zone:empty { display: none; }
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
