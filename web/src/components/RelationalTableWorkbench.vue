<template>
  <main class="relation-workbench" data-testid="workbench-table">
    <section class="relation-toolbar">
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

      <div v-if="activeView === 'data'" class="relation-toolbar__actions">
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
        <n-button size="small" type="primary" :disabled="!table" @click="showInsert = !showInsert">
          {{ showInsert ? 'Close insert' : 'Insert row' }}
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeView"
      :items="relationSections"
      aria-label="关系表工作区"
      @update:model-value="activeView = $event as RelationView"
    />

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="clearPendingOperations"
      @confirm="confirmPendingOperations"
    />

    <section v-if="activeView === 'data' && showInsert && table" class="relation-insert">
      <div class="relation-insert__head">
        <div>
          <n-text class="relation-insert__title">New row</n-text>
          <n-text depth="3" class="relation-insert__hint">Values are staged first and committed in one transaction.</n-text>
        </div>
        <n-space size="small">
          <n-button size="small" secondary @click="resetInsertDraft">Reset</n-button>
          <n-button size="small" type="primary" @click="stageInsert">Stage insert</n-button>
        </n-space>
      </div>
      <div class="relation-insert__grid">
        <label v-for="column in tableColumns" :key="column.name" class="relation-field">
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

    <template v-if="activeView === 'data'">
      <section class="relation-grid-shell">
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

      <footer class="relation-pager">
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

    <RelationalSchemaDesigner
      v-else-if="activeView === 'designer'"
      :target-db="targetDb"
      :table="table"
      :loading="loading"
      @refresh-schema="emit('refreshSchema')"
      @open-sql="emit('openSql', $event)"
    />

    <RelationalIndexManager
      v-else-if="activeView === 'indexes'"
      :target-db="targetDb"
      :table="table"
      :loading="loading"
      @refresh-schema="emit('refreshSchema')"
      @open-sql="emit('openSql', $event)"
    />

    <RelationalImportExport
      v-else-if="activeView === 'import'"
      :target-db="targetDb"
      :table="table"
      @refresh-schema="emit('refreshSchema')"
    />

    <RelationalErDiagram
      v-else-if="activeView === 'er'"
      :table="table"
      :tables="tables"
      @refresh-schema="emit('refreshSchema')"
    />

    <RelationalDdlExport
      v-else
      :target-db="targetDb"
      :table="table"
      :tables="tables"
      @open-sql="emit('openSql', $event)"
    />

    <WorkbenchHistoryDrawer
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { computed, h, onMounted, reactive, ref, watch } from 'vue';
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
}>(), {
  tables: () => [],
  loading: false,
});

const emit = defineEmits<{
  openSql: [sql: string];
  refreshSchema: [];
}>();

type SortDirection = 'asc' | 'desc';
type RelationView = 'data' | 'designer' | 'indexes' | 'import' | 'er' | 'ddl';
type DraftRow = Record<string, unknown>;

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

const pageSizeOptions: SelectOption[] = [
  { label: '25 rows', value: 25 },
  { label: '50 rows', value: 50 },
  { label: '100 rows', value: 100 },
  { label: '200 rows', value: 200 },
];

const tableColumns = computed(() =>
  [...(props.table?.columns ?? [])].sort((a, b) => a.ordinal - b.ordinal));

const primaryKeyColumns = computed(() => props.table?.primaryKey ?? []);

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

const hasNextPage = computed(() => gridRows.value.length >= pageSize.value);

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  if (!props.table || pendingOperations.value.length === 0) return null;
  const items: WriteApprovalItem[] = pendingOperations.value.map((operation) => ({
    id: operation.id,
    command: operation.sql,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `table_${props.targetDb}_${props.table.name}_${pendingOperations.value.map((item) => item.id).join('_')}`,
    title: 'Relation table edit batch',
    target: `${props.targetDb}.${props.table.name}`,
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
      column.isNullable ? ' · NULL' : '',
    ].join('')),
  ]);
}

function renderCell(row: GridRow, column: TableColumnInfo) {
  if (editingRows[row.__rowKey] && !column.isPrimaryKey) {
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
  if (editingRows[row.__rowKey]) {
    return h(NSpace, { size: 6, wrap: false }, {
      default: () => [
        h(NButton, { size: 'tiny', type: 'primary', onClick: () => stageUpdate(row) }, { default: () => 'Stage' }),
        h(NButton, { size: 'tiny', quaternary: true, onClick: () => cancelEdit(row) }, { default: () => 'Cancel' }),
      ],
    });
  }

  const canEdit = primaryKeyColumns.value.length > 0;
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
  if (!props.table || !props.targetDb) return;
  loadingRows.value = true;
  errorMsg.value = '';
  try {
    const request = buildBrowseRequest();
    lastBrowseSql.value = request.sql;
    const result = await execDataSql(auth.api, props.targetDb, request.sql, request.parameters);
    rowsResult.value = result;
    latestResult.value = result;
    latestResultSql.value = request.sql;
    ranOnce.value = true;
    if (result.error) {
      errorMsg.value = result.error.message;
    }
  } catch (error) {
    errorMsg.value = error instanceof Error ? error.message : '加载表数据失败';
  } finally {
    loadingRows.value = false;
  }
}

function buildBrowseRequest(): SqlStatementRequest {
  const table = requireTable();
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
  editDrafts[row.__rowKey] = createDraftFromRow(row);
  editingRows[row.__rowKey] = true;
}

function cancelEdit(row: GridRow): void {
  delete editDrafts[row.__rowKey];
  delete editingRows[row.__rowKey];
}

function stageInsert(): void {
  if (!props.table) return;
  const values = collectDraftValues(insertDraft, tableColumns.value);
  if (!values.ok) {
    message.error(values.message);
    return;
  }

  const opId = makeOperationId('insert');
  const parameters: SqlParameters = {};
  const placeholders = tableColumns.value.map((column, index) => {
    const paramName = makeParamName('ins', column.name, index, opId);
    parameters[paramName] = sqlParameterFromValue(values.values[column.name]);
    return `@${paramName}`;
  });
  const sql = [
    `INSERT INTO ${formatSqlIdentifier(props.table.name)} (`,
    `  ${tableColumns.value.map((column) => formatSqlIdentifier(column.name)).join(', ')}`,
    ') VALUES (',
    `  ${placeholders.join(', ')}`,
    ');',
  ].join('\n');

  pendingOperations.value.push({
    id: opId,
    action: 'insert',
    sql,
    parameters,
    label: 'Insert row',
    detail: `${tableColumns.value.length} values`,
    severity: 'write',
  });
  resetInsertDraft();
  showInsert.value = false;
}

function stageUpdate(row: GridRow): void {
  if (!props.table) return;
  const draft = editDrafts[row.__rowKey];
  const editableColumns = tableColumns.value.filter((column) => !column.isPrimaryKey);
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
    detail: changedColumns.map((column) => column.name).join(', '),
    severity: 'write',
  });
  cancelEdit(row);
}

function stageDelete(row: GridRow): void {
  if (!props.table) return;
  const where = buildPrimaryKeyWhere(row, 'pk_delete');
  if (!where.ok) {
    message.error(where.message);
    return;
  }

  const opId = makeOperationId('delete');
  pendingOperations.value.push({
    id: opId,
    action: 'delete',
    sql: [
      `DELETE FROM ${formatSqlIdentifier(props.table.name)}`,
      `WHERE ${where.sql};`,
    ].join('\n'),
    parameters: where.parameters,
    label: 'Delete row',
    detail: primaryKeyColumns.value.map((column) => `${column}=${formatSqlValue(row[column])}`).join(', '),
    severity: 'danger',
  });
}

async function confirmPendingOperations(): Promise<void> {
  if (!props.table || pendingOperations.value.length === 0) return;
  confirmBusy.value = true;
  errorMsg.value = '';
  const operations = [...pendingOperations.value];
  const statements: SqlStatementRequest[] = [
    { sql: 'BEGIN' },
    ...operations.map((operation) => ({
      sql: operation.sql,
      parameters: operation.parameters,
    })),
    { sql: 'COMMIT' },
  ];
  const command = statements.map((statement) => statement.sql).join('\n');

  try {
    const results = await execDataSqlBatch(auth.api, props.targetDb, statements);
    const errorResult = results.find((result) => result.error);
    const affected = results.reduce((sum, result) =>
      sum + Math.max(result.end?.recordsAffected ?? 0, 0), 0);
    const elapsed = results.reduce((sum, result) => sum + (result.end?.elapsedMs ?? 0), 0);

    latestResultSql.value = command;
    latestResult.value = errorResult ?? {
      columns: [],
      rows: [],
      hasColumns: false,
      error: null,
      end: {
        type: 'end',
        rowCount: 0,
        recordsAffected: affected,
        elapsedMs: elapsed,
      },
    };
    ranOnce.value = true;

    recordHistory(errorResult ? 'error' : 'success', command, affected, elapsed, errorResult?.error?.message ?? '');

    if (errorResult?.error) {
      errorMsg.value = errorResult.error.message;
      message.error(errorResult.error.message);
      return;
    }

    message.success(`Committed ${operations.length} staged edit${operations.length === 1 ? '' : 's'}.`);
    pendingOperations.value = [];
    await loadRows();
    emit('refreshSchema');
  } catch (error) {
    const messageText = error instanceof Error ? error.message : '提交关系表编辑失败';
    errorMsg.value = messageText;
    recordHistory('error', command, 0, 0, messageText);
  } finally {
    confirmBusy.value = false;
  }
}

function recordHistory(
  status: 'success' | 'error',
  command: string,
  recordsAffected: number,
  elapsedMs: number,
  error: string,
): void {
  history.record({
    kind: 'operation',
    status,
    title: `${props.table?.name ?? 'table'} edit batch`,
    target: props.table?.name ?? '',
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'table',
    action: pendingOperations.value.map((operation) => operation.action).join(', '),
    command,
    summary: error || `${pendingOperations.value.length} staged edits · affected ${recordsAffected}`,
    recordsAffected,
    elapsedMs,
  });
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}

function resetInsertDraft(): void {
  for (const key of Object.keys(insertDraft)) {
    delete insertDraft[key];
  }
  for (const column of tableColumns.value) {
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
  emit('openSql', entry.command);
}

function resetForTable(): void {
  page.value = 1;
  filterText.value = '';
  filterColumn.value = '';
  sortDirection.value = 'asc';
  sortColumn.value = primaryKeyColumns.value[0] ?? tableColumns.value[0]?.name ?? '';
  pendingOperations.value = [];
  rowsResult.value = null;
  latestResult.value = null;
  latestResultSql.value = '';
  lastBrowseSql.value = '';
  showInsert.value = false;
  for (const key of Object.keys(editingRows)) delete editingRows[key];
  for (const key of Object.keys(editDrafts)) delete editDrafts[key];
  resetInsertDraft();
}

watch(
  () => [props.targetDb, props.table?.name] as const,
  () => {
    resetForTable();
    void loadRows();
  },
);

watch(tableColumns, () => {
  resetInsertDraft();
});

onMounted(() => {
  resetForTable();
  void loadRows();
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
  padding: 8px 12px;
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
