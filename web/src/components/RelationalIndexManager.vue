<template>
  <section class="relation-index-manager">
    <header class="relation-index-manager__header">
      <div>
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="info" :bordered="false">INDEXES</n-tag>
          <n-text class="relation-index-manager__title">{{ table?.name ?? 'No table selected' }}</n-text>
        </n-space>
        <n-text depth="3" class="relation-index-manager__meta">
          {{ targetDb || 'database' }} · {{ indexes.length }} indexes · {{ pendingAction ? '1 staged action' : 'no staged action' }}
        </n-text>
      </div>
      <n-space size="small" align="center" :wrap="true">
        <n-button size="small" secondary :disabled="!pendingAction || pendingAction.kind !== 'sql'" @click="emitOpenStagedSql">
          Open SQL
        </n-button>
        <n-button size="small" tertiary :disabled="!pendingAction" @click="clearPendingAction">
          Clear
        </n-button>
      </n-space>
    </header>

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="busy"
      @cancel="clearPendingAction"
      @confirm="confirmPendingAction"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="relation-index-manager__alert"
      @close="errorMsg = ''"
    />

    <div class="relation-index-manager__body">
      <section class="relation-index-manager__panel">
        <div class="relation-index-manager__panel-head">
          <div>
            <n-text class="relation-index-manager__section-title">Declared indexes</n-text>
            <n-text depth="3" class="relation-index-manager__hint">Indexes are derived from rowstore data and can be rebuilt.</n-text>
          </div>
          <n-button size="small" secondary :disabled="!table" @click="$emit('refreshSchema')">Refresh</n-button>
        </div>

        <n-empty v-if="!table" description="Select a table from Explorer to manage indexes." />
        <n-empty v-else-if="indexes.length === 0" description="This table has no secondary indexes yet." />

        <div v-else class="relation-index-manager__index-list">
          <article
            v-for="index in indexes"
            :key="index.name"
            class="relation-index-manager__index-card"
            :class="{ 'is-json': Boolean(index.jsonPath), 'is-unique': index.isUnique }"
          >
            <div class="relation-index-manager__index-main">
              <div>
                <strong>{{ index.name }}</strong>
                <span>{{ indexLabel(index) }}</span>
              </div>
              <n-space size="small" align="center" :wrap="false">
                <n-tag v-if="index.isUnique" size="tiny" type="warning" :bordered="false">UNIQUE</n-tag>
                <n-tag v-if="index.jsonPath" size="tiny" type="info" :bordered="false">JSON</n-tag>
              </n-space>
            </div>
            <div class="relation-index-manager__index-actions">
              <n-button size="tiny" secondary :disabled="!index.rebuildable" @click="stageRebuildIndex(index)">
                Rebuild
              </n-button>
              <n-button size="tiny" tertiary type="error" @click="stageDropIndex(index)">
                Drop
              </n-button>
            </div>
          </article>
        </div>
      </section>

      <section class="relation-index-manager__panel">
        <div class="relation-index-manager__panel-head">
          <div>
            <n-text class="relation-index-manager__section-title">Create index</n-text>
            <n-text depth="3" class="relation-index-manager__hint">Generate CREATE INDEX or CREATE JSON INDEX DDL.</n-text>
          </div>
          <n-button size="small" type="primary" :disabled="!table" @click="stageCreateIndex">Stage create</n-button>
        </div>

        <div class="relation-index-manager__form-grid">
          <label class="index-field">
            <span>Kind</span>
            <n-select v-model:value="createKind" size="small" :options="indexKindOptions" />
          </label>
          <label class="index-field">
            <span>Name</span>
            <n-input v-model:value="createName" size="small" placeholder="idx_devices_tenant" />
          </label>
          <label class="index-field index-field--check">
            <span>Options</span>
            <n-checkbox v-model:checked="createIfNotExists">IF NOT EXISTS</n-checkbox>
            <n-checkbox v-if="createKind === 'secondary'" v-model:checked="createUnique">UNIQUE</n-checkbox>
          </label>
        </div>

        <div v-if="createKind === 'secondary'" class="relation-index-manager__form-grid">
          <label class="index-field index-field--wide">
            <span>Columns</span>
            <n-select
              v-model:value="createColumns"
              multiple
              filterable
              size="small"
              :options="columnOptions"
              placeholder="Choose one or more columns"
            />
          </label>
        </div>

        <div v-else class="relation-index-manager__form-grid">
          <label class="index-field">
            <span>JSON column</span>
            <n-select v-model:value="jsonColumn" size="small" :options="jsonColumnOptions" />
          </label>
          <label class="index-field">
            <span>JSON path</span>
            <n-input v-model:value="jsonPath" size="small" placeholder="$.site" />
          </label>
        </div>

        <section class="relation-index-manager__preview">
          <div class="relation-index-manager__panel-head">
            <div>
              <n-text class="relation-index-manager__section-title">Action preview</n-text>
              <n-text depth="3" class="relation-index-manager__hint">Confirm staged index changes before execution.</n-text>
            </div>
          </div>
          <pre class="relation-index-manager__sql">{{ pendingAction?.command ?? 'No staged index action yet.' }}</pre>
        </section>
      </section>
    </div>

    <WorkbenchResultPanel
      class="relation-index-manager__result"
      title="Index action result"
      :sql="latestSql"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${table?.name ?? 'indexes'}`"
      empty-description="Create, drop, or rebuild an index to see results."
      @clear-error="latestResult = null"
    />
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NCheckbox,
  NEmpty,
  NInput,
  NSelect,
  NSpace,
  NTag,
  NText,
  useMessage,
  type SelectOption,
} from 'naive-ui';
import {
  runMaintenance,
  type TableIndexInfo,
  type TableInfo,
} from '@/api/schema';
import {
  execDataSqlBatch,
  isValidIdentifier,
  type SqlResultSet,
  type SqlStatementRequest,
} from '@/api/sql';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
} from '@/utils/writeApproval';

const props = withDefaults(defineProps<{
  targetDb: string;
  table: TableInfo | null;
  loading?: boolean;
}>(), {
  loading: false,
});

const emit = defineEmits<{
  refreshSchema: [];
  openSql: [sql: string];
}>();

type IndexActionKind = 'sql' | 'maintenance';

interface PendingIndexAction {
  id: string;
  kind: IndexActionKind;
  command: string;
  label: string;
  detail: string;
  severity: 'write' | 'danger';
  maintenance?: {
    owner: string;
    name: string;
  };
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const indexKindOptions: SelectOption[] = [
  { label: 'Secondary index', value: 'secondary' },
  { label: 'JSON path index', value: 'json' },
];

const createKind = ref<'secondary' | 'json'>('secondary');
const createName = ref('');
const createIfNotExists = ref(true);
const createUnique = ref(false);
const createColumns = ref<string[]>([]);
const jsonColumn = ref('');
const jsonPath = ref('$.');
const pendingAction = ref<PendingIndexAction | null>(null);
const busy = ref(false);
const errorMsg = ref('');
const latestResult = ref<SqlResultSet | null>(null);
const latestSql = ref('');
const ranOnce = ref(false);

const indexes = computed(() => props.table?.indexes ?? []);
const tableColumns = computed(() =>
  [...(props.table?.columns ?? [])].sort((a, b) => a.ordinal - b.ordinal));

const columnOptions = computed<SelectOption[]>(() =>
  tableColumns.value.map((column) => ({
    label: `${column.name} · ${column.dataType}${column.isPrimaryKey ? ' · PK' : ''}`,
    value: column.name,
  })));

const jsonColumnOptions = computed<SelectOption[]>(() =>
  tableColumns.value
    .filter((column) => column.dataType.toUpperCase() === 'JSON')
    .map((column) => ({
      label: column.name,
      value: column.name,
    })));

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  if (!pendingAction.value) return null;
  const item: WriteApprovalItem = {
    id: pendingAction.value.id,
    command: pendingAction.value.command,
    severity: pendingAction.value.severity,
    label: pendingAction.value.label,
    detail: pendingAction.value.detail,
  };
  return createWriteApprovalPlan({
    id: `index_${props.targetDb}_${pendingAction.value.id}`,
    title: 'Relation index action',
    target: props.table ? `${props.targetDb}.${props.table.name}` : props.targetDb,
    items: [item],
  });
});

const resultSummary = computed(() => {
  if (!latestResult.value) return pendingAction.value ? '1 staged action' : 'Ready';
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    return `affected ${Math.max(latestResult.value.end.recordsAffected, 0)} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

function stageCreateIndex(): void {
  const table = requireTable();
  const name = createName.value.trim();
  if (!isValidIdentifier(name)) {
    message.error('Index name must start with a letter and contain only letters, digits, and underscores.');
    return;
  }
  if (indexes.value.some((index) => index.name === name)) {
    message.error(`Index ${name} already exists.`);
    return;
  }

  if (createKind.value === 'json') {
    stageCreateJsonIndex(table, name);
    return;
  }

  if (createColumns.value.length === 0) {
    message.error('Choose at least one index column.');
    return;
  }
  const sql = [
    'CREATE',
    createUnique.value ? 'UNIQUE' : '',
    'INDEX',
    createIfNotExists.value ? 'IF NOT EXISTS' : '',
    formatSqlIdentifier(name),
    'ON',
    formatSqlIdentifier(table.name),
    `(${createColumns.value.map(formatSqlIdentifier).join(', ')});`,
  ].filter(Boolean).join(' ');
  stageAction(sql, 'Create index', `${name} on ${createColumns.value.join(', ')}`, 'write');
}

function stageCreateJsonIndex(table: TableInfo, name: string): void {
  if (!jsonColumn.value) {
    message.error('Choose a JSON column.');
    return;
  }
  const path = jsonPath.value.trim();
  if (!path.startsWith('$.')) {
    message.error('JSON path must start with $.');
    return;
  }
  const sql = [
    'CREATE JSON INDEX',
    createIfNotExists.value ? 'IF NOT EXISTS' : '',
    formatSqlIdentifier(name),
    'ON',
    formatSqlIdentifier(table.name),
    `(${formatSqlIdentifier(jsonColumn.value)}, ${quoteSqlString(path)});`,
  ].filter(Boolean).join(' ');
  stageAction(sql, 'Create JSON index', `${name} on ${jsonColumn.value}->${path}`, 'write');
}

function stageDropIndex(index: TableIndexInfo): void {
  const table = requireTable();
  stageAction(
    `DROP INDEX ${formatSqlIdentifier(index.name)} ON ${formatSqlIdentifier(table.name)};`,
    'Drop index',
    index.name,
    'danger',
  );
}

function stageRebuildIndex(index: TableIndexInfo): void {
  const table = requireTable();
  pendingAction.value = {
    id: makeActionId(),
    kind: 'maintenance',
    command: `REBUILD INDEX ${formatSqlIdentifier(index.name)} ON ${formatSqlIdentifier(table.name)}`,
    label: 'Rebuild index',
    detail: index.name,
    severity: 'write',
    maintenance: {
      owner: table.name,
      name: index.name,
    },
  };
}

async function confirmPendingAction(): Promise<void> {
  const action = pendingAction.value;
  if (!action || !props.targetDb) return;

  busy.value = true;
  errorMsg.value = '';
  try {
    if (action.kind === 'maintenance') {
      await executeMaintenanceAction(action);
    } else {
      await executeSqlAction(action);
    }
  } catch (error) {
    const messageText = error instanceof Error ? error.message : 'Index action failed.';
    errorMsg.value = messageText;
    latestSql.value = action.command;
    latestResult.value = {
      columns: [],
      rows: [],
      hasColumns: false,
      end: null,
      error: {
        type: 'error',
        code: 'index_action_failed',
        message: messageText,
      },
    };
    ranOnce.value = true;
    recordHistory('error', action.command, 0, 0, messageText);
  } finally {
    busy.value = false;
  }
}

async function executeSqlAction(action: PendingIndexAction): Promise<void> {
  const statements: SqlStatementRequest[] = [{ sql: action.command }];
  const results = await execDataSqlBatch(auth.api, props.targetDb, statements);
  const result = results[0] ?? emptySuccessResult(0, 0);
  latestSql.value = action.command;
  latestResult.value = result;
  ranOnce.value = true;
  recordHistory(result.error ? 'error' : 'success', action.command, result.end?.recordsAffected ?? 0, result.end?.elapsedMs ?? 0, result.error?.message ?? '');

  if (result.error) {
    errorMsg.value = result.error.message;
    message.error(result.error.message);
    return;
  }

  message.success(`${action.label} applied.`);
  pendingAction.value = null;
  emit('refreshSchema');
}

async function executeMaintenanceAction(action: PendingIndexAction): Promise<void> {
  if (!action.maintenance) return;
  const started = performance.now();
  const result = await runMaintenance(auth.api, props.targetDb, {
    operation: 'rebuild_index',
    targetModel: 'table',
    targetOwner: action.maintenance.owner,
    targetName: action.maintenance.name,
  });
  const elapsed = performance.now() - started;
  latestSql.value = action.command;
  latestResult.value = result.success
    ? emptySuccessResult(0, elapsed)
    : {
      columns: [],
      rows: [],
      hasColumns: false,
      end: null,
      error: {
        type: 'error',
        code: result.status,
        message: result.message,
      },
    };
  ranOnce.value = true;
  recordHistory(result.success ? 'success' : 'error', action.command, 0, elapsed, result.success ? '' : result.message);

  if (!result.success) {
    errorMsg.value = result.message;
    message.warning(result.message);
    return;
  }

  message.success(result.message || 'Index rebuilt.');
  pendingAction.value = null;
  emit('refreshSchema');
}

function emitOpenStagedSql(): void {
  if (!pendingAction.value) return;
  emit('openSql', pendingAction.value.command.endsWith(';')
    ? pendingAction.value.command
    : `${pendingAction.value.command};`);
}

function clearPendingAction(): void {
  pendingAction.value = null;
}

function stageAction(command: string, label: string, detail: string, severity: 'write' | 'danger'): void {
  pendingAction.value = {
    id: makeActionId(),
    kind: 'sql',
    command,
    label,
    detail,
    severity,
  };
}

function indexLabel(index: TableIndexInfo): string {
  const columns = index.columns.join(', ');
  return index.jsonPath ? `${columns} -> ${index.jsonPath}` : columns;
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
    title: pendingAction.value?.label ?? 'Index action',
    target: props.table?.name ?? '',
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'index-manager',
    action: pendingAction.value?.detail ?? '',
    command,
    summary: error || `${pendingAction.value?.label ?? 'Index action'} · affected ${recordsAffected}`,
    recordsAffected,
    elapsedMs,
  });
}

function requireTable(): TableInfo {
  if (!props.table) throw new Error('No table selected.');
  return props.table;
}

function quoteSqlString(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

function makeActionId(): string {
  return `idx_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function resetCreateDraft(): void {
  createKind.value = 'secondary';
  createName.value = '';
  createIfNotExists.value = true;
  createUnique.value = false;
  createColumns.value = [];
  jsonColumn.value = String(jsonColumnOptions.value[0]?.value ?? '');
  jsonPath.value = '$.';
}

watch(
  () => props.table?.name,
  () => {
    pendingAction.value = null;
    resetCreateDraft();
  },
  { immediate: true },
);
</script>

<style scoped>
.relation-index-manager {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.relation-index-manager__header,
.relation-index-manager__panel-head,
.relation-index-manager__index-main,
.relation-index-manager__index-actions {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.relation-index-manager__header {
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfdff;
}

.relation-index-manager__title,
.relation-index-manager__section-title {
  font-weight: 800;
  color: var(--sndb-ink-strong);
}

.relation-index-manager__title {
  font-size: 15px;
}

.relation-index-manager__section-title {
  display: block;
  font-size: 13px;
}

.relation-index-manager__meta,
.relation-index-manager__hint {
  font-size: 12px;
}

.relation-index-manager__alert {
  margin: 10px 12px 0;
}

.relation-index-manager__body {
  display: grid;
  grid-template-columns: minmax(340px, 1fr) minmax(340px, 0.95fr);
  gap: 12px;
  flex: 1;
  min-height: 0;
  padding: 12px;
  overflow: auto;
}

.relation-index-manager__panel,
.relation-index-manager__preview {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  padding: 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
  background: #fff;
}

.relation-index-manager__preview {
  background: #fbfdff;
}

.relation-index-manager__index-list {
  display: flex;
  flex-direction: column;
  gap: 9px;
}

.relation-index-manager__index-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-left: 3px solid rgba(15, 23, 42, 0.16);
  border-radius: 6px;
  background: #fbfdff;
}

.relation-index-manager__index-card.is-unique {
  border-left-color: #d9822b;
}

.relation-index-manager__index-card.is-json {
  border-left-color: #2c7be5;
}

.relation-index-manager__index-main strong {
  display: block;
  color: #22384d;
  font-size: 13px;
}

.relation-index-manager__index-main span {
  display: block;
  overflow: hidden;
  max-width: 480px;
  color: var(--sndb-ink-soft);
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.relation-index-manager__index-actions {
  justify-content: flex-end;
}

.relation-index-manager__form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 10px;
}

.index-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.index-field span {
  color: #496171;
  font-size: 11px;
  font-weight: 800;
  text-transform: uppercase;
}

.index-field--wide {
  grid-column: 1 / -1;
}

.index-field--check {
  justify-content: end;
}

.index-field--check :deep(.n-checkbox) {
  margin-top: 4px;
}

.relation-index-manager__sql {
  max-height: 180px;
  min-height: 88px;
  margin: 0;
  padding: 10px 12px;
  overflow: auto;
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 6px;
  background: #f8fbff;
  color: #20384d;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.55;
}

.relation-index-manager__result {
  flex: 0 0 230px;
  min-height: 210px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1080px) {
  .relation-index-manager__body {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .relation-index-manager__header,
  .relation-index-manager__panel-head,
  .relation-index-manager__index-main,
  .relation-index-manager__index-actions {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
