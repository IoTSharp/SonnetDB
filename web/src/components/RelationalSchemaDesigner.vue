<template>
  <section class="schema-designer">
    <header class="schema-designer__header">
      <div>
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="info" :bordered="false">DESIGNER</n-tag>
          <n-text class="schema-designer__title">{{ table?.name ?? 'Create relation table' }}</n-text>
        </n-space>
        <n-text depth="3" class="schema-designer__meta">
          {{ targetDb || 'database' }} · {{ tableColumns.length }} current columns · {{ pendingDdl.length }} staged DDL
        </n-text>
      </div>
      <n-space size="small" align="center" :wrap="true">
        <n-button size="small" secondary :disabled="pendingDdl.length === 0" @click="emitOpenStagedSql">
          Open SQL
        </n-button>
        <n-button size="small" tertiary @click="resetDrafts">Reset</n-button>
      </n-space>
    </header>

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="busy"
      @cancel="clearPendingDdl"
      @confirm="confirmPendingDdl"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="schema-designer__alert"
      @close="errorMsg = ''"
    />

    <div class="schema-designer__content">
      <section class="schema-designer__panel schema-designer__panel--create">
        <div class="schema-designer__panel-head">
          <div>
            <n-text class="schema-designer__section-title">Create table</n-text>
            <n-text depth="3" class="schema-designer__hint">Build a CREATE TABLE statement with a required primary key.</n-text>
          </div>
          <n-button size="small" type="primary" @click="stageCreateTable">Stage create</n-button>
        </div>

        <div class="schema-designer__form-grid">
          <label class="schema-field schema-field--wide">
            <span>Table name</span>
            <n-input v-model:value="createTableName" size="small" placeholder="devices" />
          </label>
          <label class="schema-field schema-field--check">
            <span>Options</span>
            <n-checkbox v-model:checked="createIfNotExists">IF NOT EXISTS</n-checkbox>
          </label>
        </div>

        <div class="schema-designer__draft-table">
          <div class="schema-designer__draft-row schema-designer__draft-row--head">
            <span>Name</span>
            <span>Type</span>
            <span>Nullable</span>
            <span>Primary key</span>
            <span />
          </div>
          <div
            v-for="column in createColumns"
            :key="column.id"
            class="schema-designer__draft-row"
          >
            <n-input v-model:value="column.name" size="small" placeholder="column_name" />
            <n-select v-model:value="column.dataType" size="small" :options="dataTypeOptions" />
            <n-checkbox
              :checked="column.nullable"
              :disabled="column.primaryKey"
              @update:checked="column.nullable = $event"
            />
            <n-checkbox
              :checked="column.primaryKey"
              @update:checked="setCreateColumnPrimaryKey(column, $event)"
            />
            <n-button size="tiny" quaternary :disabled="createColumns.length === 1" @click="removeCreateColumn(column.id)">
              Remove
            </n-button>
          </div>
        </div>
        <n-button size="small" secondary @click="addCreateColumn">Add column</n-button>
      </section>

      <section class="schema-designer__panel">
        <div class="schema-designer__panel-head">
          <div>
            <n-text class="schema-designer__section-title">Alter table</n-text>
            <n-text depth="3" class="schema-designer__hint">
              Generate ALTER TABLE ADD / DROP / RENAME COLUMN and RENAME TABLE statements.
            </n-text>
          </div>
          <n-tag size="small" :bordered="false">{{ table ? 'existing table' : 'select a table' }}</n-tag>
        </div>

        <n-empty v-if="!table" description="Select an existing table from Explorer to alter its schema." />

        <template v-else>
          <div class="schema-designer__current">
            <article
              v-for="column in tableColumns"
              :key="column.name"
              class="schema-designer__column-card"
              :class="{ 'is-primary': column.isPrimaryKey }"
            >
              <span>{{ column.name }}</span>
              <small>
                {{ column.dataType }}{{ column.isNullable ? ' · NULL' : ' · NOT NULL' }}{{ column.isPrimaryKey ? ' · PK' : '' }}
              </small>
            </article>
          </div>

          <div class="schema-designer__operations">
            <section class="schema-designer__operation">
              <div class="schema-designer__operation-head">
                <strong>Rename table</strong>
                <n-button size="small" secondary @click="stageRenameTable">Stage</n-button>
              </div>
              <n-input v-model:value="renameTableName" size="small" :placeholder="table.name" />
            </section>

            <section class="schema-designer__operation">
              <div class="schema-designer__operation-head">
                <strong>Add column</strong>
                <n-button size="small" secondary @click="stageAddColumn">Stage</n-button>
              </div>
              <div class="schema-designer__form-grid">
                <label class="schema-field">
                  <span>Name</span>
                  <n-input v-model:value="addColumn.name" size="small" placeholder="site" />
                </label>
                <label class="schema-field">
                  <span>Type</span>
                  <n-select v-model:value="addColumn.dataType" size="small" :options="dataTypeOptions" />
                </label>
                <label class="schema-field schema-field--check">
                  <span>Nullable</span>
                  <n-checkbox v-model:checked="addColumn.nullable">NULL</n-checkbox>
                </label>
                <label class="schema-field">
                  <span>Default</span>
                  <n-input v-model:value="addColumn.defaultValue" size="small" placeholder="required for NOT NULL" />
                </label>
              </div>
            </section>

            <section class="schema-designer__operation">
              <div class="schema-designer__operation-head">
                <strong>Rename column</strong>
                <n-button size="small" secondary @click="stageRenameColumn">Stage</n-button>
              </div>
              <div class="schema-designer__form-grid">
                <label class="schema-field">
                  <span>Column</span>
                  <n-select v-model:value="renameColumn.oldName" size="small" :options="alterableColumnOptions" />
                </label>
                <label class="schema-field">
                  <span>New name</span>
                  <n-input v-model:value="renameColumn.newName" size="small" placeholder="display_name" />
                </label>
              </div>
            </section>

            <section class="schema-designer__operation">
              <div class="schema-designer__operation-head">
                <strong>Drop column</strong>
                <n-button size="small" tertiary type="error" @click="stageDropColumn">Stage</n-button>
              </div>
              <div class="schema-designer__form-grid">
                <label class="schema-field">
                  <span>Column</span>
                  <n-select v-model:value="dropColumn.name" size="small" :options="droppableColumnOptions" />
                </label>
                <label class="schema-field schema-field--check">
                  <span>Options</span>
                  <n-checkbox v-model:checked="dropColumn.ifExists">IF EXISTS</n-checkbox>
                </label>
              </div>
            </section>
          </div>
        </template>
      </section>

      <section class="schema-designer__panel schema-designer__panel--preview">
        <div class="schema-designer__panel-head">
          <div>
            <n-text class="schema-designer__section-title">DDL preview</n-text>
            <n-text depth="3" class="schema-designer__hint">Review generated SQL before confirmation.</n-text>
          </div>
          <n-button size="small" quaternary :disabled="pendingDdl.length === 0" @click="clearPendingDdl">
            Clear
          </n-button>
        </div>
        <pre class="schema-designer__sql">{{ pendingSqlText || 'No staged DDL yet.' }}</pre>
      </section>
    </div>

    <WorkbenchResultPanel
      class="schema-designer__result"
      title="Schema DDL result"
      :sql="latestSql"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${table?.name ?? 'schema'}`"
      empty-description="Stage and confirm DDL to see results."
      @clear-error="latestResult = null"
    />
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
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
import type { TableInfo } from '@/api/schema';
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
import {
  useWorkbenchHistoryStore,
} from '@/stores/workbenchHistory';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
} from '@/utils/writeApproval';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';

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

type DdlSeverity = 'write' | 'danger';

interface PendingDdl {
  id: string;
  sql: string;
  label: string;
  detail: string;
  severity: DdlSeverity;
}

interface CreateColumnDraft {
  id: string;
  name: string;
  dataType: string;
  nullable: boolean;
  primaryKey: boolean;
}

interface AddColumnDraft {
  name: string;
  dataType: string;
  nullable: boolean;
  defaultValue: string;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const dataTypeOptions: SelectOption[] = [
  { label: 'STRING', value: 'STRING' },
  { label: 'INT', value: 'INT' },
  { label: 'FLOAT', value: 'FLOAT' },
  { label: 'BOOL', value: 'BOOL' },
  { label: 'DATETIME', value: 'DATETIME' },
  { label: 'JSON', value: 'JSON' },
  { label: 'BLOB', value: 'BLOB' },
];

const createTableName = ref('');
const createIfNotExists = ref(true);
const createColumns = ref<CreateColumnDraft[]>([]);
const renameTableName = ref('');
const addColumn = reactive<AddColumnDraft>({
  name: '',
  dataType: 'STRING',
  nullable: true,
  defaultValue: '',
});
const renameColumn = reactive({
  oldName: '',
  newName: '',
});
const dropColumn = reactive({
  name: '',
  ifExists: true,
});
const pendingDdl = ref<PendingDdl[]>([]);
const busy = ref(false);
const errorMsg = ref('');
const latestResult = ref<SqlResultSet | null>(null);
const latestSql = ref('');
const ranOnce = ref(false);

const tableColumns = computed(() =>
  [...(props.table?.columns ?? [])].sort((a, b) => a.ordinal - b.ordinal));

const alterableColumnOptions = computed<SelectOption[]>(() =>
  tableColumns.value
    .filter((column) => !column.isPrimaryKey)
    .map((column) => ({
      label: `${column.name} · ${column.dataType}`,
      value: column.name,
    })));

const droppableColumnOptions = alterableColumnOptions;

const pendingSqlText = computed(() => pendingDdl.value.map((item) => item.sql).join('\n\n'));

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  if (pendingDdl.value.length === 0) return null;
  const items: WriteApprovalItem[] = pendingDdl.value.map((item) => ({
    id: item.id,
    command: item.sql,
    severity: item.severity,
    label: item.label,
    detail: item.detail,
  }));
  return createWriteApprovalPlan({
    id: `schema_${props.targetDb}_${pendingDdl.value.map((item) => item.id).join('_')}`,
    title: 'Relation schema DDL',
    target: props.table ? `${props.targetDb}.${props.table.name}` : props.targetDb,
    items,
  });
});

const resultSummary = computed(() => {
  if (!latestResult.value) return pendingDdl.value.length ? `${pendingDdl.value.length} staged DDL` : 'Ready';
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    return `affected ${Math.max(latestResult.value.end.recordsAffected, 0)} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

function resetDrafts(): void {
  createTableName.value = props.table ? `${props.table.name}_new` : '';
  createIfNotExists.value = true;
  createColumns.value = [
    { id: makeDraftId(), name: 'id', dataType: 'INT', nullable: false, primaryKey: true },
    { id: makeDraftId(), name: 'name', dataType: 'STRING', nullable: false, primaryKey: false },
  ];
  renameTableName.value = props.table?.name ?? '';
  addColumn.name = '';
  addColumn.dataType = 'STRING';
  addColumn.nullable = true;
  addColumn.defaultValue = '';
  renameColumn.oldName = String(alterableColumnOptions.value[0]?.value ?? '');
  renameColumn.newName = '';
  dropColumn.name = String(droppableColumnOptions.value[0]?.value ?? '');
  dropColumn.ifExists = true;
}

function addCreateColumn(): void {
  createColumns.value.push({
    id: makeDraftId(),
    name: '',
    dataType: 'STRING',
    nullable: true,
    primaryKey: false,
  });
}

function removeCreateColumn(id: string): void {
  if (createColumns.value.length <= 1) return;
  createColumns.value = createColumns.value.filter((column) => column.id !== id);
}

function setCreateColumnPrimaryKey(column: CreateColumnDraft, value: boolean): void {
  column.primaryKey = value;
  if (value) {
    column.nullable = false;
  }
}

function stageCreateTable(): void {
  const validation = validateCreateTable();
  if (!validation.ok) {
    message.error(validation.message);
    return;
  }

  const tableName = createTableName.value.trim();
  const columns = createColumns.value.map((column) => {
    const nullability = column.primaryKey || !column.nullable ? 'NOT NULL' : 'NULL';
    return `  ${formatSqlIdentifier(column.name.trim())} ${column.dataType} ${nullability}`;
  });
  const primaryKey = createColumns.value
    .filter((column) => column.primaryKey)
    .map((column) => formatSqlIdentifier(column.name.trim()));
  const sql = [
    `CREATE TABLE ${createIfNotExists.value ? 'IF NOT EXISTS ' : ''}${formatSqlIdentifier(tableName)} (`,
    [...columns, `  PRIMARY KEY (${primaryKey.join(', ')})`].join(',\n'),
    ');',
  ].join('\n');

  stageDdl(sql, 'Create table', tableName, 'write');
}

function stageRenameTable(): void {
  const table = requireTable();
  const nextName = renameTableName.value.trim();
  if (!isValidIdentifier(nextName)) {
    message.error('Table name must start with a letter and contain only letters, digits, and underscores.');
    return;
  }
  if (nextName === table.name) {
    message.info('Table name is unchanged.');
    return;
  }
  stageDdl(
    `ALTER TABLE ${formatSqlIdentifier(table.name)} RENAME TO ${formatSqlIdentifier(nextName)};`,
    'Rename table',
    `${table.name} -> ${nextName}`,
    'danger',
  );
}

function stageAddColumn(): void {
  const table = requireTable();
  const name = addColumn.name.trim();
  if (!isValidIdentifier(name)) {
    message.error('Column name must start with a letter and contain only letters, digits, and underscores.');
    return;
  }
  if (tableColumns.value.some((column) => column.name === name)) {
    message.error(`Column ${name} already exists.`);
    return;
  }
  if (!addColumn.nullable && !addColumn.defaultValue.trim()) {
    message.error('NOT NULL ADD COLUMN requires a DEFAULT value.');
    return;
  }

  let defaultExpression = '';
  try {
    defaultExpression = addColumn.defaultValue.trim()
      ? ` DEFAULT ${formatDefaultExpression(addColumn.defaultValue, addColumn.dataType)}`
      : '';
  } catch (error) {
    message.error(error instanceof Error ? error.message : 'Invalid DEFAULT value.');
    return;
  }
  const nullability = addColumn.nullable ? 'NULL' : 'NOT NULL';
  stageDdl(
    `ALTER TABLE ${formatSqlIdentifier(table.name)} ADD COLUMN ${formatSqlIdentifier(name)} ${addColumn.dataType} ${nullability}${defaultExpression};`,
    'Add column',
    `${name} ${addColumn.dataType}`,
    'write',
  );
}

function stageRenameColumn(): void {
  const table = requireTable();
  const oldName = renameColumn.oldName;
  const newName = renameColumn.newName.trim();
  if (!oldName) {
    message.error('Choose a column to rename.');
    return;
  }
  if (!isValidIdentifier(newName)) {
    message.error('New column name must start with a letter and contain only letters, digits, and underscores.');
    return;
  }
  if (tableColumns.value.some((column) => column.name === newName)) {
    message.error(`Column ${newName} already exists.`);
    return;
  }
  stageDdl(
    `ALTER TABLE ${formatSqlIdentifier(table.name)} RENAME COLUMN ${formatSqlIdentifier(oldName)} TO ${formatSqlIdentifier(newName)};`,
    'Rename column',
    `${oldName} -> ${newName}`,
    'danger',
  );
}

function stageDropColumn(): void {
  const table = requireTable();
  const name = dropColumn.name;
  if (!name) {
    message.error('Choose a column to drop.');
    return;
  }
  stageDdl(
    `ALTER TABLE ${formatSqlIdentifier(table.name)} DROP COLUMN ${dropColumn.ifExists ? 'IF EXISTS ' : ''}${formatSqlIdentifier(name)};`,
    'Drop column',
    name,
    'danger',
  );
}

async function confirmPendingDdl(): Promise<void> {
  if (!props.targetDb || pendingDdl.value.length === 0) return;

  const operations = [...pendingDdl.value];
  const statements: SqlStatementRequest[] = operations.map((operation) => ({ sql: operation.sql }));
  const command = operations.map((operation) => operation.sql).join('\n\n');
  busy.value = true;
  errorMsg.value = '';
  try {
    const results = await execDataSqlBatch(auth.api, props.targetDb, statements);
    const errorResult = results.find((result) => result.error);
    const elapsed = results.reduce((sum, result) => sum + (result.end?.elapsedMs ?? 0), 0);
    const affected = results.reduce((sum, result) => sum + Math.max(result.end?.recordsAffected ?? 0, 0), 0);

    latestSql.value = command;
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

    message.success(`Applied ${operations.length} DDL statement${operations.length === 1 ? '' : 's'}.`);
    pendingDdl.value = [];
    emit('refreshSchema');
  } catch (error) {
    const messageText = error instanceof Error ? error.message : 'Schema DDL failed.';
    errorMsg.value = messageText;
    recordHistory('error', command, 0, 0, messageText);
  } finally {
    busy.value = false;
  }
}

function emitOpenStagedSql(): void {
  if (!pendingSqlText.value) return;
  emit('openSql', pendingSqlText.value);
}

function clearPendingDdl(): void {
  pendingDdl.value = [];
}

function stageDdl(sql: string, label: string, detail: string, severity: DdlSeverity): void {
  pendingDdl.value.push({
    id: makeDraftId(),
    sql,
    label,
    detail,
    severity,
  });
}

function validateCreateTable(): { ok: true } | { ok: false; message: string } {
  const tableName = createTableName.value.trim();
  if (!isValidIdentifier(tableName)) {
    return { ok: false, message: 'Table name must start with a letter and contain only letters, digits, and underscores.' };
  }
  if (createColumns.value.length === 0) {
    return { ok: false, message: 'Add at least one column.' };
  }

  const names = new Set<string>();
  for (const column of createColumns.value) {
    const name = column.name.trim();
    if (!isValidIdentifier(name)) {
      return { ok: false, message: `Invalid column name: ${name || '(empty)'}.` };
    }
    if (names.has(name)) {
      return { ok: false, message: `Duplicate column name: ${name}.` };
    }
    names.add(name);
  }

  if (!createColumns.value.some((column) => column.primaryKey)) {
    return { ok: false, message: 'Choose at least one primary key column.' };
  }
  return { ok: true };
}

function formatDefaultExpression(value: string, dataType: string): string {
  const trimmed = value.trim();
  if (/^(STRING|DATETIME|JSON|BLOB)$/i.test(dataType)) {
    if (dataType === 'JSON') {
      try {
        JSON.parse(trimmed);
      } catch {
        throw new Error('JSON default must be valid JSON text.');
      }
    }
    return quoteSqlString(trimmed);
  }

  if (/^BOOL$/i.test(dataType)) {
    if (!/^(true|false)$/i.test(trimmed)) {
      throw new Error('BOOL default must be TRUE or FALSE.');
    }
    return trimmed.toUpperCase();
  }

  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed)) {
    throw new Error(`${dataType} default must be numeric.`);
  }
  if (/^INT$/i.test(dataType) && !Number.isInteger(parsed)) {
    throw new Error('INT default must be an integer.');
  }
  return String(parsed);
}

function quoteSqlString(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
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
    title: props.table ? `${props.table.name} schema DDL` : 'Create relation table',
    target: props.table?.name ?? createTableName.value.trim(),
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'schema-designer',
    action: pendingDdl.value.map((operation) => operation.label).join(', '),
    command,
    summary: error || `${pendingDdl.value.length} DDL statements · affected ${recordsAffected}`,
    recordsAffected,
    elapsedMs,
  });
}

function requireTable(): TableInfo {
  if (!props.table) throw new Error('No table selected.');
  return props.table;
}

function makeDraftId(): string {
  return `ddl_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

watch(
  () => props.table?.name,
  () => resetDrafts(),
  { immediate: true },
);
</script>

<style scoped>
.schema-designer {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.schema-designer__header,
.schema-designer__panel-head,
.schema-designer__operation-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.schema-designer__header {
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfdff;
}

.schema-designer__title,
.schema-designer__section-title {
  font-weight: 800;
  color: var(--sndb-ink-strong);
}

.schema-designer__title {
  font-size: 15px;
}

.schema-designer__section-title {
  display: block;
  font-size: 13px;
}

.schema-designer__meta,
.schema-designer__hint {
  font-size: 12px;
}

.schema-designer__alert {
  margin: 10px 12px 0;
}

.schema-designer__content {
  display: grid;
  grid-template-columns: minmax(300px, 0.9fr) minmax(360px, 1.1fr);
  gap: 12px;
  flex: 1;
  min-height: 0;
  padding: 12px;
  overflow: auto;
}

.schema-designer__panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  padding: 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 7px;
  background: #fff;
}

.schema-designer__panel--create {
  background: #fbfdff;
}

.schema-designer__panel--preview {
  grid-column: 1 / -1;
}

.schema-designer__form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 10px;
}

.schema-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.schema-field span {
  color: #496171;
  font-size: 11px;
  font-weight: 800;
  text-transform: uppercase;
}

.schema-field--wide {
  grid-column: span 2;
}

.schema-field--check {
  justify-content: end;
}

.schema-designer__draft-table {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.schema-designer__draft-row {
  display: grid;
  grid-template-columns: minmax(130px, 1fr) 124px 80px 96px 74px;
  gap: 8px;
  align-items: center;
  min-width: 620px;
}

.schema-designer__draft-row--head {
  color: #6a7f91;
  font-size: 10px;
  font-weight: 900;
  text-transform: uppercase;
}

.schema-designer__current {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 8px;
}

.schema-designer__column-card {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  padding: 8px 9px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-left: 3px solid rgba(15, 23, 42, 0.14);
  border-radius: 6px;
  background: #fbfdff;
}

.schema-designer__column-card.is-primary {
  border-left-color: #18a058;
  background: rgba(24, 160, 88, 0.06);
}

.schema-designer__column-card span {
  overflow: hidden;
  color: #22384d;
  font-size: 12px;
  font-weight: 800;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.schema-designer__column-card small {
  overflow: hidden;
  color: var(--sndb-ink-soft);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.schema-designer__operations {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
}

.schema-designer__operation {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.schema-designer__operation-head strong {
  font-size: 12px;
  color: var(--sndb-ink-strong);
}

.schema-designer__sql {
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

.schema-designer__result {
  flex: 0 0 230px;
  min-height: 210px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1180px) {
  .schema-designer__content,
  .schema-designer__operations {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .schema-designer__header,
  .schema-designer__panel-head,
  .schema-designer__operation-head {
    flex-direction: column;
    align-items: stretch;
  }

  .schema-field--wide {
    grid-column: auto;
  }
}
</style>
