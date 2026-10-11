<template>
  <main
    class="relation-workbench"
    data-testid="workbench-table"
    data-shell="five-zone"
    :data-page-state="relationState"
    :data-database="targetDb"
    :data-resource-key="table?.name ?? ''"
    :data-legacy-key="table ? `table:${table.name}` : ''"
  >
    <section class="relation-toolbar" data-zone="toolbar">
      <div class="relation-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="info" :bordered="false">TABLE</n-tag>
          <n-text class="relation-toolbar__title">{{ table?.name ?? 'No table selected' }}</n-text>
          <n-tag v-if="primaryKeyColumns.length" size="tiny" :bordered="false">
            PK {{ primaryKeyColumns.join(', ') }}
          </n-tag>
        </n-space>
        <n-text depth="3" class="relation-toolbar__meta">
          {{ targetDb || 'database' }} · {{ tableColumns.length }} columns · {{ pendingOperations.length }} staged edits
        </n-text>
      </div>

      <div v-if="activeView === 'data' && !permissionDenied" class="relation-toolbar__actions">
        <n-select
          v-model:value="filterColumn"
          size="small"
          :options="filterColumnOptions"
          class="relation-toolbar__select"
        />
        <n-input
          v-model:value="filterText"
          size="small"
          clearable
          placeholder="Filter"
          class="relation-toolbar__filter"
          @keydown.enter="applyFilter"
        />
        <n-select
          v-model:value="sortColumn"
          size="small"
          :options="sortColumnOptions"
          class="relation-toolbar__select"
          @update:value="reloadFirstPage"
        />
        <n-button size="small" quaternary :disabled="!sortColumn" @click="toggleSortDirection">
          {{ sortDirection.toUpperCase() }}
        </n-button>
        <n-button size="small" secondary @click="applyFilter">Apply</n-button>
        <n-button size="small" secondary :loading="loadingRows" @click="loadRows">Refresh</n-button>
        <n-button size="small" type="primary" :disabled="!canWrite" @click="showInsert = !showInsert">
          {{ showInsert ? 'Close insert' : 'Insert row' }}
        </n-button>
        <n-button v-if="pendingOperations.length" size="small" secondary :disabled="!canWrite" @click="previewPendingOperations">
          Preview staged edits
        </n-button>
        <n-button v-if="pendingOperations.length" size="small" quaternary :disabled="!canWrite" @click="clearPendingOperations">
          Discard staged edits
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      data-zone="tabs"
      :model-value="activeView"
      :items="relationSections"
      aria-label="关系表工作区"
      @update:model-value="activeView = $event as RelationView"
    />

    <WriteApprovalPanel
      v-if="previewVisible && previewPlan"
      data-zone="approval"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="hidePendingPreview"
      @confirm="confirmPendingOperations"
    />

    <section v-if="activeView === 'data' && showInsert && table && canWrite" class="relation-insert" data-zone="context">
      <div class="relation-insert__head">
        <div>
          <n-text class="relation-insert__title">New row</n-text>
          <n-text depth="3" class="relation-insert__hint">{{ previewEnabled ? 'Preview: approve and submit one parameterized row once.' : 'Values are staged first and committed in one transaction.' }}</n-text>
        </div>
        <n-space size="small">
          <n-button size="small" secondary @click="resetInsertDraft">Reset</n-button>
          <n-button size="small" type="primary" @click="stageInsert">Stage insert</n-button>
        </n-space>
      </div>
      <div class="relation-insert__grid">
        <label v-for="column in insertableColumns" :key="column.name" class="relation-field">
          <span>
            {{ column.name }}
            <small>{{ column.dataType }}{{ column.isNullable ? ' · nullable' : '' }}</small>
          </span>

          <n-input-number
            v-if="isNumericColumn(column)"
            :value="numberDraftValue(insertDraft[column.name])"
            size="small"
            :show-button="false"
            :placeholder="column.isNullable ? 'NULL' : column.dataType"
            @update:value="setDraftValue(insertDraft, column.name, $event)"
          />
          <n-select
            v-else-if="isBooleanColumn(column)"
            :value="booleanDraftValue(insertDraft[column.name], column.isNullable)"
            size="small"
            :options="booleanOptions(column)"
            @update:value="setBooleanDraftValue(insertDraft, column.name, $event)"
          />
          <n-input
            v-else
            :value="textDraftValue(insertDraft[column.name])"
            size="small"
            :type="isLongTextColumn(column) ? 'textarea' : 'text'"
            :autosize="isLongTextColumn(column) ? { minRows: 1, maxRows: 3 } : false"
            :placeholder="column.isNullable ? 'NULL' : column.dataType"
            @update:value="setDraftValue(insertDraft, column.name, $event)"
          />

          <n-button
            v-if="column.isNullable"
            size="tiny"
            quaternary
            class="relation-field__null"
            @click="setDraftValue(insertDraft, column.name, null)"
          >
            NULL
          </n-button>
        </label>
      </div>
    </section>

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="relation-alert"
      @close="errorMsg = ''"
    />

    <section v-if="permissionDenied" class="relation-restricted" data-zone="center">
      <n-empty description="当前数据库的关系表读取权限不足。表行、结果、草稿与审批已隐藏。" />
    </section>

    <template v-else-if="activeView === 'data'">
      <section class="relation-grid-shell" data-zone="center">
        <n-empty v-if="!table" description="Select a table from Explorer." />
        <n-data-table
          v-else
          :columns="dataColumns"
          :data="gridRows"
          :loading="loadingRows || loading"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          size="small"
          remote
          flex-height
          class="relation-grid"
        />
      </section>

      <footer class="relation-pager" data-zone="status">
        <div class="relation-pager__meta">
          <span>{{ browseSummary }}</span>
          <span v-if="lastBrowseSql" class="relation-pager__sql">{{ lastBrowseSql }}</span>
        </div>
        <n-space size="small" align="center">
          <n-select
            v-model:value="pageSize"
            size="small"
            :options="pageSizeOptions"
            class="relation-pager__size"
            @update:value="reloadFirstPage"
          />
          <n-button size="small" :disabled="page <= 1 || loadingRows" @click="previousPage">Previous</n-button>
          <n-tag size="small" :bordered="false">Page {{ page }}</n-tag>
          <n-button size="small" :disabled="!hasNextPage || loadingRows" @click="nextPage">Next</n-button>
        </n-space>
      </footer>

      <WorkbenchResultPanel
        class="relation-result"
        data-zone="result"
        title="Relation SQL result"
        :sql="latestResultSql"
        :result="latestResult"
        :ran-once="ranOnce"
        :summary="resultSummary"
        :file-name="`${targetDb}_${table?.name ?? 'table'}`"
        empty-description="Browse or edit table rows to see SQL results."
        @clear-error="latestResult = null"
      />
    </template>

    <section v-else-if="readOnly && activeView !== 'ddl'" class="relation-restricted" data-zone="center">
      <n-empty description="当前宿主为只读。数据浏览、当前结果导出与 DDL 查看可用；此子工作台尚未提供只读权限合同。" />
    </section>

    <RelationalSchemaDesigner
      v-else-if="activeView === 'designer'"
      :key="resourceInstanceKey"
      data-zone="center"
      :target-db="targetDb"
      :table="table"
      :loading="loading"
      @refresh-schema="emit('refreshSchema')"
      @open-sql="emit('openSql', $event)"
    />

    <RelationalIndexManager
      v-else-if="activeView === 'indexes'"
      :key="resourceInstanceKey"
      data-zone="center"
      :target-db="targetDb"
      :table="table"
      :loading="loading"
      @refresh-schema="emit('refreshSchema')"
      @open-sql="emit('openSql', $event)"
    />

    <RelationalImportExport
      v-else-if="activeView === 'import'"
      :key="resourceInstanceKey"
      data-zone="center"
      :target-db="targetDb"
      :table="table"
      @refresh-schema="emit('refreshSchema')"
    />

    <RelationalErDiagram
      v-else-if="activeView === 'er'"
      :key="resourceInstanceKey"
      data-zone="center"
      :table="table"
      :tables="tables"
      @refresh-schema="emit('refreshSchema')"
    />

    <RelationalDdlExport
      v-else
      :key="resourceInstanceKey"
      data-zone="center"
      :target-db="targetDb"
      :table="table"
      :tables="tables"
      @open-sql="emit('openSql', $event)"
    />

    <footer class="relation-statebar" data-zone="status">
      <span>{{ stateDescriptor.summary }}</span>
      <span>{{ stateDescriptor.primary }}</span>
    </footer>

    <WorkbenchHistoryDrawer
      v-if="!permissionDenied"
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import type { AxiosInstance } from 'axios';
import {
  NAlert,
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import type { TableColumnInfo, TableInfo } from '@/api/schema';
import {
  execDataSql,
  execDataSqlBatch,
  rowsToObjects,
  sqlParameterFromValue,
  type SqlParameters,
  type SqlResultSet,
  type SqlStatementRequest,
} from '@/api/sql';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import { previewEnabled } from '@/preview/policy';
import RelationalDdlExport from '@/components/RelationalDdlExport.vue';
import RelationalErDiagram from '@/components/RelationalErDiagram.vue';
import RelationalImportExport from '@/components/RelationalImportExport.vue';
import RelationalIndexManager from '@/components/RelationalIndexManager.vue';
import RelationalSchemaDesigner from '@/components/RelationalSchemaDesigner.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
} from '@/utils/writeApproval';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';
import { formatSqlValue } from '@/utils/sqlValue';

const props = withDefaults(defineProps<{
  targetDb: string;
  table: TableInfo | null;
  tables?: TableInfo[];
  loading?: boolean;
  /** 只读宿主保留 SELECT 与结果导出，禁用暂存、提交和无权限合同的子工作台。 */
  readOnly?: boolean;
  /** 无读取权限时清理并隐藏表行、结果、草稿与审批。 */
  permissionDenied?: boolean;
}>(), {
  tables: () => [],
  loading: false,
  readOnly: false,
  permissionDenied: false,
});

const emit = defineEmits<{
  openSql: [sql: string];
  refreshSchema: [];
}>();

type SortDirection = 'asc' | 'desc';
type RelationView = 'data' | 'designer' | 'indexes' | 'import' | 'er' | 'ddl';
type DraftRow = Record<string, unknown>;
type RelationWorkbenchState = 'normal' | 'empty' | 'error' | 'permission' | 'readonly' | 'longContent';

const RelationPreviewMaxRows = 200;
const RelationLongContentChars = 8192;
const relationStateContract = {
  normal: { primary: '浏览当前页', summary: '保留数据库与表名原始拼写；行修改先暂存，确认后才提交。' },
  empty: { primary: '调整筛选条件', summary: '没有表或当前页没有行；保留筛选和排序输入，不自动创建资源。' },
  error: { primary: '检查并重试读取', summary: '保留筛选和排序；失败或未知写入结果须先核对服务器，旧审批不会重放。' },
  permission: { primary: '查看数据库权限', summary: '隐藏表行、结果、草稿与审批；不从导航菜单推断权限。' },
  readonly: { primary: '导出当前结果', summary: 'SELECT 与当前结果导出可用；行修改和无只读合同的子工作台禁用。' },
  longContent: { primary: '缩小查询范围', summary: '每页最多 200 行；长字段滚动或折叠，满页仅表示可请求下一页，不代表全量行数。' },
} satisfies Record<RelationWorkbenchState, { primary: string; summary: string }>;

interface RelationContext {
  identity: string;
  generation: number;
  database: string;
  table: string;
  connectionId: string;
  connectionName: string;
  api: AxiosInstance;
  sessionToken: string | undefined;
  schemaSignature: string;
}

interface GridRow extends Record<string, unknown> {
  __rowKey: string;
  __rowNumber: number;
}

interface PendingOperation {
  id: string;
  action: 'insert' | 'update' | 'delete';
  sql: string;
  parameters: SqlParameters;
  label: string;
  detail: string;
  severity: 'write' | 'danger';
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const loadingRows = ref(false);
const confirmBusy = ref(false);
const permissionFailure = ref(false);
const readOnly = computed(() => props.readOnly);
const permissionDenied = computed(() => props.permissionDenied || permissionFailure.value);
const resourceIdentity = computed(() => JSON.stringify([
  connections.activeProfileId, connections.activeBaseUrl, props.targetDb, props.table?.name ?? '',
]));
const contextGeneration = ref(0);
const resourceInstanceKey = computed(() => `${resourceIdentity.value}:${contextGeneration.value}`);
const schemaSignature = computed(() => JSON.stringify([props.table?.columns ?? [], props.table?.primaryKey ?? []]));
let readRequestId = 0;
let disposed = false;
let readController: AbortController | null = null;
let writeController: AbortController | null = null;
let pendingContext: RelationContext | null = null;
let writeInFlight = false;
const errorMsg = ref('');
const activeView = ref<RelationView>('data');
const relationSections: WorkbenchSectionTab[] = [
  { key: 'data', label: '数据' },
  { key: 'designer', label: '设计器' },
  { key: 'indexes', label: '索引' },
  { key: 'import', label: '导入 / 导出' },
  { key: 'er', label: 'ER 图' },
  { key: 'ddl', label: 'DDL' },
];
const rowsResult = ref<SqlResultSet | null>(null);
const latestResult = ref<SqlResultSet | null>(null);
const latestResultSql = ref('');
const lastBrowseSql = ref('');
const ranOnce = ref(false);
const showInsert = ref(false);
const historyVisible = ref(false);
const page = ref(1);
const pageSize = ref(50);
const sortColumn = ref('');
const sortDirection = ref<SortDirection>('asc');
const filterColumn = ref('');
const filterText = ref('');
const insertDraft = reactive<DraftRow>({});
const editDrafts = reactive<Record<string, DraftRow>>({});
const editingRows = reactive<Record<string, boolean>>({});
const pendingOperations = ref<PendingOperation[]>([]);
const previewVisible = ref(false);
const canWrite = computed(() => Boolean(props.table && props.targetDb)
  && !readOnly.value && !permissionDenied.value && !confirmBusy.value && !errorMsg.value);

const pageSizeOptions: SelectOption[] = [
  { label: '25 rows', value: 25 },
  { label: '50 rows', value: 50 },
  { label: '100 rows', value: 100 },
  { label: '200 rows', value: 200 },
];

const tableColumns = computed(() =>
  [...(permissionDenied.value ? [] : props.table?.columns ?? [])].sort((a, b) => a.ordinal - b.ordinal));
const insertableColumns = computed(() =>
  tableColumns.value.filter((column) => !column.isAutoIncrement && !column.isRowVersion));

const primaryKeyColumns = computed(() => permissionDenied.value ? [] : props.table?.primaryKey ?? []);

const filterColumnOptions = computed<SelectOption[]>(() => [
  { label: 'All searchable columns', value: '' },
  ...tableColumns.value.map((column) => ({
    label: `${column.name} · ${column.dataType}`,
    value: column.name,
  })),
]);

const sortColumnOptions = computed<SelectOption[]>(() =>
  tableColumns.value.map((column) => ({
    label: column.name,
    value: column.name,
  })));

const gridRows = computed<GridRow[]>(() => {
  if (!rowsResult.value) return [];
  return rowsToObjects<Record<string, unknown>>(rowsResult.value).map((row, index) => ({
    ...row,
    __rowKey: makeRowKey(row, index),
    __rowNumber: (page.value - 1) * pageSize.value + index + 1,
  }));
});

const hasNextPage = computed(() => !permissionDenied.value && !rowsResult.value?.error
  && Boolean(rowsResult.value?.end) && gridRows.value.length >= pageSize.value);
const relationState = computed<RelationWorkbenchState>(() => {
  if (permissionDenied.value) return 'permission';
  if (!props.table || !props.targetDb) return 'empty';
  if (errorMsg.value || latestResult.value?.error) return 'error';
  if (ranOnce.value && rowsResult.value?.end && gridRows.value.length === 0) return 'empty';
  if (readOnly.value) return 'readonly';
  if (hasNextPage.value || rowsResult.value?.end?.truncated || tableColumns.value.length > 12
    || gridRows.value.some((row) => tableColumns.value.some((column) =>
      formatSqlValue(row[column.name]).length > RelationLongContentChars))) return 'longContent';
  return 'normal';
});
const stateDescriptor = computed(() => relationStateContract[relationState.value]);

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  const operationCount = pendingOperations.value.length;
  if (!previewVisible.value || !canWrite.value || !pendingContext || !isContextCurrent(pendingContext)
    || operationCount === 0) return null;
  const items: WriteApprovalItem[] = pendingOperations.value.map((operation) => ({
    id: operation.id,
    command: operation.sql,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `table_${pendingContext.database}_${pendingContext.table}_${pendingOperations.value.map((item) => item.id).join('_')}`,
    title: 'Relation table edit batch',
    target: `${pendingContext.database}.${pendingContext.table}`,
    items,
  });
});

const browseSummary = computed(() => {
  if (rowsResult.value?.error) return rowsResult.value.error.message;
  if (rowsResult.value?.end) {
    return `${gridRows.value.length} visible rows · ${rowsResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return ranOnce.value ? `${gridRows.value.length} visible rows` : 'Ready';
});

const resultSummary = computed(() => {
  if (!latestResult.value) return browseSummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    const affected = latestResult.value.end.recordsAffected >= 0
      ? `affected ${latestResult.value.end.recordsAffected}`
      : `${latestResult.value.end.rowCount} rows`;
    return `${affected} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

const dataColumns = computed<DataTableColumns<GridRow>>(() => {
  const columns: DataTableColumns<GridRow> = [
    {
      title: '#',
      key: '__rowNumber',
      width: 58,
      fixed: 'left',
      render: (row) => h('span', { class: 'relation-grid__row-number' }, String(row.__rowNumber)),
    },
    ...tableColumns.value.map((column) => ({
      title: () => renderColumnTitle(column),
      key: column.name,
      minWidth: columnWidth(column),
      ellipsis: { tooltip: true },
      render: (row: GridRow) => renderCell(row, column),
    })),
    {
      title: 'Actions',
      key: '__actions',
      width: 188,
      fixed: 'right',
      render: (row) => renderRowActions(row),
    },
  ];
  return columns;
});

function renderColumnTitle(column: TableColumnInfo) {
  return h('div', { class: 'relation-column-title' }, [
    h('span', column.name),
    h('small', [
      column.dataType,
      column.isPrimaryKey ? ' · PK' : '',
      column.isAutoIncrement ? ' · AUTO' : '',
      column.isNullable ? ' · NULL' : '',
    ].join('')),
  ]);
}

function renderCell(row: GridRow, column: TableColumnInfo) {
  if (editingRows[row.__rowKey]
    && !column.isPrimaryKey
    && !column.isAutoIncrement
    && !column.isRowVersion) {
    const draft = editDrafts[row.__rowKey];
    return renderDraftEditor(column, draft, (value) => {
      draft[column.name] = value;
    });
  }
  return renderReadonlyValue(row[column.name], column);
}

function renderReadonlyValue(value: unknown, column: TableColumnInfo) {
  if (value === null || value === undefined) {
    return h(NTag, { size: 'tiny', bordered: false }, { default: () => 'NULL' });
  }
  if (isBooleanColumn(column)) {
    return h(NTag, {
      size: 'tiny',
      type: value ? 'success' : 'warning',
      bordered: false,
    }, { default: () => (value ? 'TRUE' : 'FALSE') });
  }
  if (isNumericColumn(column)) {
    return h('code', { class: 'relation-cell relation-cell--number' }, formatSqlValue(value));
  }
  return h('span', { class: 'relation-cell' }, formatSqlValue(value));
}

function renderDraftEditor(
  column: TableColumnInfo,
  draft: DraftRow,
  update: (value: unknown) => void,
) {
  const value = draft[column.name];
  const children = [
    isNumericColumn(column)
      ? h(NInputNumber, {
        value: numberDraftValue(value),
        size: 'small',
        showButton: false,
        placeholder: column.dataType,
        'onUpdate:value': update,
      })
      : isBooleanColumn(column)
        ? h(NSelect, {
          value: booleanDraftValue(value, column.isNullable),
          size: 'small',
          options: booleanOptions(column),
          'onUpdate:value': (next: string) => update(booleanValueFromSelect(next)),
        })
        : h(NInput, {
          value: textDraftValue(value),
          size: 'small',
          type: isLongTextColumn(column) ? 'textarea' : 'text',
          autosize: isLongTextColumn(column) ? { minRows: 1, maxRows: 3 } : false,
          placeholder: column.dataType,
          'onUpdate:value': update,
        }),
  ];

  if (column.isNullable) {
    children.push(h(NButton, {
      size: 'tiny',
      quaternary: true,
      class: 'relation-cell-editor__null',
      onClick: () => update(null),
    }, { default: () => 'NULL' }));
  }

  return h('div', { class: 'relation-cell-editor' }, children);
}

function renderRowActions(row: GridRow) {
  if (previewEnabled || readOnly.value || permissionDenied.value) return null;
  if (editingRows[row.__rowKey]) {
    return h(NSpace, { size: 6, wrap: false }, {
      default: () => [
        h(NButton, { size: 'tiny', type: 'primary', onClick: () => stageUpdate(row) }, { default: () => 'Stage' }),
        h(NButton, { size: 'tiny', quaternary: true, onClick: () => cancelEdit(row) }, { default: () => 'Cancel' }),
      ],
    });
  }

  const canEdit = canWrite.value && primaryKeyColumns.value.length > 0;
  return h(NSpace, { size: 6, wrap: false }, {
    default: () => [
      h(NButton, {
        size: 'tiny',
        secondary: true,
        disabled: !canEdit,
        onClick: () => startEdit(row),
      }, { default: () => 'Edit' }),
      h(NButton, {
        size: 'tiny',
        tertiary: true,
        type: 'error',
        disabled: !canEdit,
        onClick: () => stageDelete(row),
      }, { default: () => 'Delete' }),
    ],
  });
}

function rowKey(row: GridRow): string {
  return row.__rowKey;
}

async function loadRows(): Promise<void> {
  if (!props.table || !props.targetDb || permissionDenied.value || disposed) return;
  const context = captureContext();
  const requestId = ++readRequestId;
  readController?.abort();
  const controller = new AbortController();
  readController = controller;
  loadingRows.value = true;
  errorMsg.value = '';
  try {
    const request = buildBrowseRequest();
    lastBrowseSql.value = request.sql;
    const result = await execDataSql(context.api, context.database, request.sql, request.parameters,
      controller.signal, pageSize.value);
    if (!isContextCurrent(context) || requestId !== readRequestId || permissionDenied.value) return;
    if (isPermissionFailure(result.error)) {
      permissionFailure.value = true;
      return;
    }
    const boundedResult: SqlResultSet = {
      ...result,
      rows: result.error || !result.end ? [] : result.rows.slice(0, pageSize.value),
      error: result.error ?? (!result.end
        ? { code: 'incomplete_sql_response', message: '读取结果缺少完成标记，不能确认成功。' }
        : null),
      end: result.end ? {
        ...result.end,
        rowCount: Math.min(result.rows.length, pageSize.value),
        truncated: result.end.truncated || result.rows.length > pageSize.value,
      } : null,
    };
    rowsResult.value = boundedResult;
    latestResult.value = boundedResult;
    latestResultSql.value = request.sql;
    ranOnce.value = true;
    if (boundedResult.error) {
      errorMsg.value = boundedResult.error.message;
    }
  } catch (error) {
    if (!isContextCurrent(context) || requestId !== readRequestId || controller.signal.aborted) return;
    if (isPermissionFailure(error)) {
      permissionFailure.value = true;
      return;
    }
    rowsResult.value = null;
    latestResult.value = null;
    errorMsg.value = error instanceof Error ? error.message : '加载表数据失败';
  } finally {
    if (requestId === readRequestId && isContextCurrent(context)) {
      loadingRows.value = false;
      readController = null;
    }
  }
}

function buildBrowseRequest(): SqlStatementRequest {
  const table = requireTable();
  pageSize.value = pageSizeOptions.some((option) => option.value === pageSize.value)
    ? Math.min(pageSize.value, RelationPreviewMaxRows) : 50;
  page.value = Number.isSafeInteger(page.value) && page.value >= 1 ? page.value : 1;
  const parameters: SqlParameters = {
    limit: sqlParameterFromValue(pageSize.value),
    offset: sqlParameterFromValue((page.value - 1) * pageSize.value),
  };
  const projection = tableColumns.value.map((column) => formatSqlIdentifier(column.name)).join(', ');
  const where = buildFilterPredicate(parameters);
  const orderBy = sortColumn.value
    ? `ORDER BY ${formatSqlIdentifier(sortColumn.value)} ${sortDirection.value.toUpperCase()}`
    : '';
  const lines = [
    `SELECT ${projection || '*'}`,
    `FROM ${formatSqlIdentifier(table.name)}`,
    where,
    orderBy,
    'LIMIT @limit',
    'OFFSET @offset',
  ].filter(Boolean);
  return { sql: `${lines.join('\n')};`, parameters };
}

function buildFilterPredicate(parameters: SqlParameters): string {
  const text = filterText.value.trim();
  if (!text) return '';

  if (filterColumn.value) {
    const column = tableColumns.value.find((item) => item.name === filterColumn.value);
    if (!column) return '';
    const coerced = coerceInputValue(column, text, { allowNull: false });
    if (!coerced.ok) {
      throw new Error(coerced.message);
    }
    parameters.filter_value = isTextSearchColumn(column)
      ? sqlParameterFromValue(`%${text}%`)
      : sqlParameterFromValue(coerced.value);
    return `WHERE ${formatSqlIdentifier(column.name)} ${isTextSearchColumn(column) ? 'LIKE' : '='} @filter_value`;
  }

  const clauses: string[] = [];
  const textColumns = tableColumns.value.filter(isTextSearchColumn);
  if (textColumns.length > 0) {
    parameters.filter_text = sqlParameterFromValue(`%${text}%`);
    clauses.push(...textColumns.map((column) => `${formatSqlIdentifier(column.name)} LIKE @filter_text`));
  }

  const number = Number(text);
  if (Number.isFinite(number)) {
    parameters.filter_number = sqlParameterFromValue(number);
    clauses.push(...tableColumns.value
      .filter(isNumericColumn)
      .map((column) => `${formatSqlIdentifier(column.name)} = @filter_number`));
  }

  if (/^(true|false)$/i.test(text)) {
    parameters.filter_bool = sqlParameterFromValue(/^true$/i.test(text));
    clauses.push(...tableColumns.value
      .filter(isBooleanColumn)
      .map((column) => `${formatSqlIdentifier(column.name)} = @filter_bool`));
  }

  return clauses.length > 0 ? `WHERE (${clauses.join(' OR ')})` : '';
}

function applyFilter(): void {
  void reloadFirstPage();
}

async function reloadFirstPage(): Promise<void> {
  page.value = 1;
  await loadRows();
}

function toggleSortDirection(): void {
  sortDirection.value = sortDirection.value === 'asc' ? 'desc' : 'asc';
  void reloadFirstPage();
}

function previousPage(): void {
  if (page.value <= 1) return;
  page.value -= 1;
  void loadRows();
}

function nextPage(): void {
  if (!hasNextPage.value) return;
  page.value += 1;
  void loadRows();
}

function startEdit(row: GridRow): void {
  if (previewEnabled) return;
  if (!canWrite.value || writeInFlight) return;
  editDrafts[row.__rowKey] = createDraftFromRow(row);
  editingRows[row.__rowKey] = true;
}

function cancelEdit(row: GridRow): void {
  delete editDrafts[row.__rowKey];
  delete editingRows[row.__rowKey];
}

function stageInsert(): void {
  if (!props.table || !canWrite.value || writeInFlight) return;
  if (previewEnabled && insertableColumns.value.length === 0) { message.warning('本预览不支持 DEFAULT VALUES 插入。'); return; }
  if (previewEnabled && pendingOperations.value.length > 0) { message.warning('本预览一次只能审批一个插入。请先确认或丢弃现有草稿。'); return; }
  const values = collectDraftValues(insertDraft, insertableColumns.value);
  if (!values.ok) {
    message.error(values.message);
    return;
  }

  const opId = makeOperationId('insert');
  const parameters: SqlParameters = {};
  const placeholders = insertableColumns.value.map((column, index) => {
    const paramName = makeParamName('ins', column.name, index, opId);
    parameters[paramName] = sqlParameterFromValue(values.values[column.name]);
    return `@${paramName}`;
  });
  const sql = insertableColumns.value.length === 0
    ? `INSERT INTO ${formatSqlIdentifier(props.table.name)} DEFAULT VALUES;`
    : [
      `INSERT INTO ${formatSqlIdentifier(props.table.name)} (`,
      `  ${insertableColumns.value.map((column) => formatSqlIdentifier(column.name)).join(', ')}`,
      ') VALUES (',
      `  ${placeholders.join(', ')}`,
      ');',
    ].join('\n');

  capturePendingContext();
  pendingOperations.value.push({
    id: opId,
    action: 'insert',
    sql,
    parameters,
    label: 'Insert row',
    detail: previewDraftValues(insertableColumns.value, values.values),
    severity: 'write',
  });
  resetInsertDraft();
  showInsert.value = false;
  previewPendingOperations();
}

function stageUpdate(row: GridRow): void {
  if (previewEnabled) return;
  if (!props.table || !canWrite.value || writeInFlight) return;
  const draft = editDrafts[row.__rowKey];
  if (!draft) return;
  const editableColumns = tableColumns.value.filter((column) =>
    !column.isPrimaryKey && !column.isAutoIncrement && !column.isRowVersion);
  const values = collectDraftValues(draft, editableColumns);
  if (!values.ok) {
    message.error(values.message);
    return;
  }

  const changedColumns = editableColumns.filter((column) =>
    !sameValue(row[column.name], values.values[column.name]));
  if (changedColumns.length === 0) {
    message.info('No changed cells to stage.');
    return;
  }

  const where = buildPrimaryKeyWhere(row, 'pk_update');
  if (!where.ok) {
    message.error(where.message);
    return;
  }

  const opId = makeOperationId('update');
  const parameters: SqlParameters = { ...where.parameters };
  const assignments = changedColumns.map((column, index) => {
    const paramName = makeParamName('set', column.name, index, opId);
    parameters[paramName] = sqlParameterFromValue(values.values[column.name]);
    return `${formatSqlIdentifier(column.name)} = @${paramName}`;
  });

  capturePendingContext();
  pendingOperations.value.push({
    id: opId,
    action: 'update',
    sql: [
      `UPDATE ${formatSqlIdentifier(props.table.name)}`,
      `SET ${assignments.join(', ')}`,
      `WHERE ${where.sql};`,
    ].join('\n'),
    parameters,
    label: 'Update row',
    detail: previewDraftValues(changedColumns, values.values, row),
    severity: 'write',
  });
  cancelEdit(row);
  previewPendingOperations();
}

function stageDelete(row: GridRow): void {
  if (previewEnabled) return;
  if (!props.table || !canWrite.value || writeInFlight) return;
  const where = buildPrimaryKeyWhere(row, 'pk_delete');
  if (!where.ok) {
    message.error(where.message);
    return;
  }

  const opId = makeOperationId('delete');
  capturePendingContext();
  pendingOperations.value.push({
    id: opId,
    action: 'delete',
    sql: [
      `DELETE FROM ${formatSqlIdentifier(props.table.name)}`,
      `WHERE ${where.sql};`,
    ].join('\n'),
    parameters: where.parameters,
    label: 'Delete row',
    detail: previewDraftValues(tableColumns.value.filter((column) => column.isPrimaryKey), row),
    severity: 'danger',
  });
  previewPendingOperations();
}

function previewPendingOperations(): void {
  if (!canWrite.value || writeInFlight || !pendingContext || !isContextCurrent(pendingContext)
    || pendingOperations.value.length === 0) {
    if (pendingContext && !isContextCurrent(pendingContext)) clearPendingOperations();
    return;
  }
  previewVisible.value = true;
}

function hidePendingPreview(): void {
  if (confirmBusy.value || writeInFlight) return;
  previewVisible.value = false;
}

async function confirmPendingOperations(): Promise<void> {
  if (previewEnabled && (pendingOperations.value.length !== 1 || pendingOperations.value[0].action !== 'insert')) return;
  if (!previewVisible.value || !canWrite.value || writeInFlight || !pendingContext || !isContextCurrent(pendingContext)
    || pendingOperations.value.length === 0) {
    if (pendingContext && !isContextCurrent(pendingContext)) clearPendingOperations();
    return;
  }
  const context = pendingContext;
  const controller = new AbortController();
  writeController = controller;
  // 审批仅执行一次；响应缺失或失败都须核对服务器并重新暂存，不能重放原计划。
  const operations = pendingOperations.value.map((operation) => ({
    ...operation,
    parameters: Object.fromEntries(Object.entries(operation.parameters).map(([key, value]) => [key, { ...value }])),
  }));
  clearPendingOperations();
  writeInFlight = true;
  confirmBusy.value = true;
  errorMsg.value = '';
  const statements: SqlStatementRequest[] = previewEnabled ? operations.map((operation) => ({ sql: operation.sql, parameters: operation.parameters })) : [
    { sql: 'BEGIN' },
    ...operations.map((operation) => ({
      sql: operation.sql,
      parameters: operation.parameters,
    })),
    { sql: 'COMMIT' },
  ];
  const command = statements.map((statement) => statement.sql).join('\n');

  try {
    const results = previewEnabled
      ? [await execDataSql(context.api, context.database, statements[0].sql, statements[0].parameters, controller.signal, undefined, 'relation.insert.one')]
      : await execDataSqlBatch(context.api, context.database, statements, controller.signal);
    const errorResult = results.find((result) => result.error);
    const complete = results.length === statements.length && results.every((result) => Boolean(result.end));
    const unknown = controller.signal.aborted || (errorResult?.error
      ? /^(invalid_sql_response|incomplete_sql_response|http_408|http_5\d\d)$/i.test(errorResult.error.code ?? '')
      : !complete);
    const errorText = unknown
      ? unknownWriteMessage(controller.signal.aborted
        ? '客户端因上下文改变停止了请求；这不表示服务器已经取消写入。'
        : errorResult?.error?.message ?? '批次缺少完整服务器终态')
      : errorResult?.error?.message ?? '';
    // 事务内语句仅暂存修改；COMMIT 已返回实际提交数量，不能再次累加暂存计数。
    const affected = complete && !errorResult
      ? Math.max(results.at(-1)?.end?.recordsAffected ?? 0, 0) : 0;
    const elapsed = results.reduce((sum, result) => sum + (result.end?.elapsedMs ?? 0), 0);

    recordHistory(context, operations, unknown ? 'unknown' : errorText ? 'error' : 'success',
      command, affected, elapsed, errorText, unknown ? 'unknown' : complete ? 'complete' : 'partial');
    if (!isContextCurrent(context) || readOnly.value || permissionDenied.value) return;
    if (isPermissionFailure(errorResult?.error)) {
      permissionFailure.value = true;
      return;
    }
    latestResultSql.value = command;
    latestResult.value = !unknown && errorResult ? errorResult : {
      columns: [],
      rows: [],
      hasColumns: false,
      error: errorText ? { code: 'operation_outcome_unknown', message: errorText } : null,
      end: complete && !unknown ? {
        type: 'end',
        rowCount: 0,
        recordsAffected: affected,
        elapsedMs: elapsed,
      } : null,
    };
    ranOnce.value = true;

    if (errorText) {
      rowsResult.value = null;
      errorMsg.value = errorText;
      message.error(errorText);
      return;
    }

    message.success(`Committed ${operations.length} staged edit${operations.length === 1 ? '' : 's'}.`);
    await loadRows();
    if (isContextCurrent(context) && !permissionDenied.value) emit('refreshSchema');
  } catch (error) {
    const denied = isPermissionFailure(error);
    const detail = error instanceof Error ? error.message : '提交关系表编辑失败';
    const messageText = denied ? detail : unknownWriteMessage(detail);
    recordHistory(context, operations, denied ? 'error' : 'unknown', command, 0, 0,
      messageText, denied ? 'complete' : 'unknown');
    if (!isContextCurrent(context) || readOnly.value || permissionDenied.value) return;
    if (denied) permissionFailure.value = true;
    else {
      rowsResult.value = null;
      latestResultSql.value = command;
      latestResult.value = {
        columns: [], rows: [], hasColumns: false, end: null,
        error: { code: 'operation_outcome_unknown', message: messageText },
      };
      ranOnce.value = true;
      errorMsg.value = messageText;
      message.error(messageText);
    }
  } finally {
    if (writeController === controller) writeController = null;
    writeInFlight = false;
    confirmBusy.value = false;
  }
}

function recordHistory(
  context: RelationContext,
  operations: PendingOperation[],
  status: 'success' | 'error' | 'unknown',
  command: string,
  recordsAffected: number,
  elapsedMs: number,
  error: string,
  completeness: 'complete' | 'partial' | 'unknown',
): void {
  history.record({
    kind: 'operation',
    status,
    title: `${context.table} edit batch`,
    target: context.table,
    database: context.database,
    connectionId: context.connectionId,
    connectionName: context.connectionName,
    model: 'table',
    action: operations.map((operation) => operation.action).join(', '),
    command,
    summary: error || `${operations.length} staged edits · affected ${recordsAffected}`,
    recordsAffected,
    elapsedMs,
    completeness,
  });
}

function clearPendingOperations(): void {
  previewVisible.value = false;
  pendingOperations.value = [];
  pendingContext = null;
}

function captureContext(): RelationContext {
  return {
    identity: resourceIdentity.value,
    generation: contextGeneration.value,
    database: props.targetDb,
    table: props.table?.name ?? '',
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    api: auth.api,
    sessionToken: auth.state?.token,
    schemaSignature: schemaSignature.value,
  };
}

function isContextCurrent(context: RelationContext): boolean {
  return !disposed && context.generation === contextGeneration.value
    && context.identity === resourceIdentity.value && context.api === auth.api
    && context.sessionToken === auth.state?.token && context.schemaSignature === schemaSignature.value;
}

function capturePendingContext(): void {
  if (!pendingContext || !isContextCurrent(pendingContext)) {
    clearPendingOperations();
    pendingContext = captureContext();
  }
}

function isPermissionFailure(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const value = error as { code?: string; response?: { status?: number; data?: { code?: string; error?: string } } };
  return value.response?.status === 403
    || /^(http_403|forbidden|permission_denied|access_denied|insufficient_permissions)$/i.test(
      value.code ?? value.response?.data?.code ?? value.response?.data?.error ?? '');
}

function unknownWriteMessage(detail: string): string {
  return `执行结果未知，请先核对服务器终态再重新预览和审批；旧审批不会重试。${detail}`;
}

function previewDraftValues(columns: TableColumnInfo[], values: DraftRow, original?: DraftRow): string {
  return columns.map((column) => {
    const preview = (value: unknown) => {
      const text = formatSqlValue(value);
      return text.length > 256 ? `${text.slice(0, 256)}…（预览已截断）` : text;
    };
    return `${column.name} (${column.dataType}): ${original ? `${preview(original[column.name])} → ` : ''}${preview(values[column.name])}`;
  }).join('\n').slice(0, 2000);
}

function resetInsertDraft(): void {
  for (const key of Object.keys(insertDraft)) {
    delete insertDraft[key];
  }
  for (const column of insertableColumns.value) {
    insertDraft[column.name] = defaultDraftValue(column);
  }
}

function createDraftFromRow(row: GridRow): DraftRow {
  const draft: DraftRow = {};
  for (const column of tableColumns.value) {
    draft[column.name] = row[column.name] ?? null;
  }
  return draft;
}

function collectDraftValues(columnsDraft: DraftRow, columns: TableColumnInfo[]):
  | { ok: true; values: Record<string, unknown> }
  | { ok: false; message: string } {
  const values: Record<string, unknown> = {};
  for (const column of columns) {
    const coerced = coerceInputValue(column, columnsDraft[column.name], { allowNull: column.isNullable });
    if (!coerced.ok) return coerced;
    values[column.name] = coerced.value;
  }
  return { ok: true, values };
}

function coerceInputValue(
  column: TableColumnInfo,
  value: unknown,
  options: { allowNull: boolean },
): { ok: true; value: unknown } | { ok: false; message: string } {
  if (value === null || value === undefined || value === '') {
    if (options.allowNull) return { ok: true, value: null };
    if (isStringColumn(column)) return { ok: true, value: '' };
    return { ok: false, message: `${column.name} requires ${column.dataType}.` };
  }

  if (isIntegerColumn(column)) {
    const parsed = Number(value);
    if (!Number.isInteger(parsed)) {
      return { ok: false, message: `${column.name} must be an integer.` };
    }
    return { ok: true, value: parsed };
  }

  if (isFloatColumn(column)) {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      return { ok: false, message: `${column.name} must be a number.` };
    }
    return { ok: true, value: parsed };
  }

  if (isBooleanColumn(column)) {
    if (typeof value === 'boolean') return { ok: true, value };
    if (/^(true|false)$/i.test(String(value))) {
      return { ok: true, value: /^true$/i.test(String(value)) };
    }
    return { ok: false, message: `${column.name} must be TRUE or FALSE.` };
  }

  if (isJsonColumn(column)) {
    const text = String(value);
    try {
      JSON.parse(text);
    } catch {
      return { ok: false, message: `${column.name} must be valid JSON text.` };
    }
    return { ok: true, value: text };
  }

  return { ok: true, value: String(value) };
}

function buildPrimaryKeyWhere(row: GridRow, prefix: string):
  | { ok: true; sql: string; parameters: SqlParameters }
  | { ok: false; message: string } {
  if (primaryKeyColumns.value.length === 0) {
    return { ok: false, message: 'This table has no primary key; row update/delete is disabled.' };
  }

  const parameters: SqlParameters = {};
  const clauses = primaryKeyColumns.value.map((columnName, index) => {
    const column = tableColumns.value.find((item) => item.name === columnName);
    if (!column) {
      throw new Error(`Primary key column ${columnName} is not in table schema.`);
    }
    const paramName = makeParamName(prefix, columnName, index, row.__rowKey);
    parameters[paramName] = sqlParameterFromValue(row[columnName]);
    return `${formatSqlIdentifier(columnName)} = @${paramName}`;
  });

  return { ok: true, sql: clauses.join(' AND '), parameters };
}

function makeRowKey(row: Record<string, unknown>, index: number): string {
  if (primaryKeyColumns.value.length > 0) {
    return primaryKeyColumns.value.map((column) => `${column}:${formatSqlValue(row[column])}`).join('|');
  }
  return `row:${page.value}:${index}`;
}

function makeOperationId(prefix: string): string {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function makeParamName(prefix: string, column: string, index: number, salt: string): string {
  const safe = column.replace(/[^A-Za-z0-9_]/g, '_').replace(/^([^A-Za-z_])/, '_$1');
  const suffix = salt.replace(/[^A-Za-z0-9_]/g, '_').slice(-8);
  return `${prefix}_${safe}_${index}_${suffix}`;
}

function requireTable(): TableInfo {
  if (!props.table) throw new Error('No table selected.');
  return props.table;
}

function defaultDraftValue(column: TableColumnInfo): unknown {
  if (column.isNullable) return null;
  if (isBooleanColumn(column)) return false;
  if (isNumericColumn(column)) return 0;
  if (isJsonColumn(column)) return '{}';
  return '';
}

function setDraftValue(draft: DraftRow, column: string, value: unknown): void {
  draft[column] = value;
}

function setBooleanDraftValue(draft: DraftRow, column: string, value: string): void {
  draft[column] = booleanValueFromSelect(value);
}

function booleanValueFromSelect(value: string): boolean | null {
  if (value === '__null') return null;
  return value === 'true';
}

function booleanDraftValue(value: unknown, nullable: boolean): string {
  if (value === null || value === undefined) return nullable ? '__null' : 'false';
  return value ? 'true' : 'false';
}

function numberDraftValue(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function textDraftValue(value: unknown): string {
  return value === null || value === undefined ? '' : String(value);
}

function booleanOptions(column: TableColumnInfo): SelectOption[] {
  const options: SelectOption[] = [
    { label: 'TRUE', value: 'true' },
    { label: 'FALSE', value: 'false' },
  ];
  if (column.isNullable) {
    options.push({ label: 'NULL', value: '__null' });
  }
  return options;
}

function normalizedType(column: TableColumnInfo): string {
  return column.dataType.toLowerCase();
}

function isIntegerColumn(column: TableColumnInfo): boolean {
  return /^(int|int64|integer|long)$/i.test(normalizedType(column));
}

function isFloatColumn(column: TableColumnInfo): boolean {
  return /^(float|float64|double|real)$/i.test(normalizedType(column));
}

function isNumericColumn(column: TableColumnInfo): boolean {
  return isIntegerColumn(column) || isFloatColumn(column);
}

function isBooleanColumn(column: TableColumnInfo): boolean {
  return /^(bool|boolean)$/i.test(normalizedType(column));
}

function isJsonColumn(column: TableColumnInfo): boolean {
  return normalizedType(column) === 'json';
}

function isStringColumn(column: TableColumnInfo): boolean {
  return /^(string|text|datetime|blob|json)$/i.test(normalizedType(column));
}

function isTextSearchColumn(column: TableColumnInfo): boolean {
  return isStringColumn(column);
}

function isLongTextColumn(column: TableColumnInfo): boolean {
  return /^(json|blob)$/i.test(normalizedType(column));
}

function columnWidth(column: TableColumnInfo): number {
  if (isJsonColumn(column) || normalizedType(column) === 'blob') return 260;
  if (isBooleanColumn(column)) return 120;
  if (isNumericColumn(column)) return 140;
  return 180;
}

function sameValue(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (a === null || a === undefined || b === null || b === undefined) {
    return a === null && b === null;
  }
  return String(a) === String(b);
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  if (permissionDenied.value || disposed) return;
  emit('openSql', entry.command);
}

function clearResourcePayload(): void {
  ++readRequestId;
  readController?.abort();
  readController = null;
  loadingRows.value = false;
  errorMsg.value = '';
  ranOnce.value = false;
  page.value = 1;
  filterText.value = '';
  filterColumn.value = '';
  sortDirection.value = 'asc';
  sortColumn.value = primaryKeyColumns.value[0] ?? tableColumns.value[0]?.name ?? '';
  clearPendingOperations();
  rowsResult.value = null;
  latestResult.value = null;
  latestResultSql.value = '';
  lastBrowseSql.value = '';
  showInsert.value = false;
  historyVisible.value = false;
  for (const key of Object.keys(editingRows)) delete editingRows[key];
  for (const key of Object.keys(editDrafts)) delete editDrafts[key];
  resetInsertDraft();
}

watch(
  () => [resourceIdentity.value, schemaSignature.value, auth.state?.token, auth.state?.username, auth.api] as const,
  (current, previous) => {
    ++contextGeneration.value;
    // 原身份的 schema 刷新仅使草稿/审批失效，不能解除服务端返回的无权限状态。
    if (current[0] !== previous[0] || current[2] !== previous[2]
      || current[3] !== previous[3] || current[4] !== previous[4]) permissionFailure.value = false;
    clearResourcePayload();
    void loadRows();
  },
  { flush: 'sync' },
);

watch(contextGeneration, () => writeController?.abort(), { flush: 'sync' });

watch(permissionDenied, (denied) => {
  ++contextGeneration.value;
  if (denied) clearResourcePayload();
  else void loadRows();
}, { flush: 'sync' });

watch(() => props.permissionDenied, (denied, previous) => {
  if (previous && !denied) permissionFailure.value = false;
}, { flush: 'sync' });

watch(readOnly, () => {
  ++contextGeneration.value;
  clearPendingOperations();
  showInsert.value = false;
  for (const key of Object.keys(editingRows)) delete editingRows[key];
  for (const key of Object.keys(editDrafts)) delete editDrafts[key];
  resetInsertDraft();
  void loadRows();
}, { flush: 'sync' });

onMounted(() => {
  clearResourcePayload();
  void loadRows();
});

onBeforeUnmount(() => {
  disposed = true;
  writeController?.abort();
  ++contextGeneration.value;
  clearResourcePayload();
});
</script>

<style scoped>
.relation-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.relation-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfdff;
}

.relation-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.relation-toolbar__title {
  font-size: 15px;
  font-weight: 800;
  color: var(--sndb-ink-strong);
}

.relation-toolbar__meta,
.relation-insert__hint {
  font-size: 12px;
}

.relation-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.relation-toolbar__tabs {
  flex: 0 0 auto;
  min-width: 260px;
}

.relation-toolbar__select {
  width: 170px;
}

.relation-toolbar__filter {
  width: 220px;
}

.relation-insert {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #f8fbf6;
}

.relation-insert__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.relation-insert__title {
  display: block;
  font-weight: 800;
}

.relation-insert__grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 10px;
}

.relation-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.relation-field > span {
  display: flex;
  flex-direction: column;
  gap: 1px;
  color: var(--sndb-ink-strong);
  font-size: 12px;
  font-weight: 700;
}

.relation-field small {
  color: var(--sndb-ink-soft);
  font-size: 10px;
  font-weight: 500;
}

.relation-field__null {
  align-self: flex-start;
}

.relation-alert {
  margin: 10px 12px 0;
}

.relation-restricted {
  display: grid;
  flex: 1;
  min-height: 260px;
  place-items: center;
  padding: 16px;
}

.relation-statebar {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.relation-grid-shell {
  flex: 1;
  min-height: 260px;
  overflow: hidden;
}

.relation-grid {
  height: 100%;
}

.relation-grid :deep(.n-data-table-base-table-body) {
  min-height: 220px;
}

.relation-grid__row-number,
.relation-cell--number {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
}

.relation-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: middle;
  white-space: nowrap;
}

.relation-column-title {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
}

.relation-column-title span {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 12px;
  font-weight: 800;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-column-title small {
  color: var(--sndb-ink-soft);
  font-size: 10px;
  font-weight: 500;
}

.relation-cell-editor {
  display: grid;
  grid-template-columns: minmax(110px, 1fr) auto;
  gap: 4px;
  align-items: center;
}

.relation-cell-editor__null {
  min-width: 38px;
}

.relation-pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  /* 为右下角 Copilot 悬浮入口保留点击空间，分页操作在窄屏继续按既有布局换行。 */
  padding: 8px 84px 8px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.relation-pager__meta {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.relation-pager__sql {
  overflow: hidden;
  max-width: 760px;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-pager__size {
  width: 108px;
}

.relation-result {
  flex: 0 0 260px;
  min-height: 220px;
  border-top: 0;
}

@media (max-width: 980px) {
  .relation-toolbar,
  .relation-insert__head,
  .relation-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .relation-toolbar__actions {
    justify-content: flex-start;
  }

  .relation-toolbar__tabs {
    width: 100%;
  }

  .relation-toolbar__select,
  .relation-toolbar__filter {
    width: 100%;
  }
}
</style>
