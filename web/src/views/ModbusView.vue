<template>
  <div class="modbus-view">
    <header class="modbus-toolbar">
      <div>
        <h1>Modbus TCP</h1>
        <span class="runtime-state" :class="{ 'is-on': overview?.runtimeEnabled }">
          <span class="runtime-dot" />
          {{ overview?.runtimeEnabled ? 'Runtime enabled' : 'Runtime disabled' }}
        </span>
      </div>
      <div class="modbus-toolbar__actions">
        <n-select
          v-model:value="database"
          size="small"
          :options="databaseOptions"
          :loading="databaseLoading"
          placeholder="数据库"
          style="width: 210px"
        />
        <n-tooltip>
          <template #trigger>
            <n-button quaternary circle size="small" :loading="loading" @click="loadAll">
              <template #icon><RefreshCw :size="17" /></template>
            </n-button>
          </template>
          刷新 Modbus 状态
        </n-tooltip>
      </div>
    </header>

    <n-alert v-if="error" type="error" closable @close="error = ''">{{ error }}</n-alert>

    <section class="status-strip" aria-label="Modbus 状态摘要">
      <div>
        <span>Sources</span>
        <strong>{{ overview?.sources.length ?? 0 }}</strong>
      </div>
      <div>
        <span>Endpoints</span>
        <strong>{{ overview?.endpoints.length ?? 0 }}</strong>
      </div>
      <div>
        <span>Bindings</span>
        <strong>{{ overview?.bindings.length ?? 0 }}</strong>
      </div>
      <div class="is-pending">
        <span>Pending writes</span>
        <strong>{{ pendingWrites.length }}</strong>
      </div>
    </section>

    <n-tabs v-model:value="activeTab" type="line" animated class="modbus-tabs">
      <n-tab-pane name="runtime" tab="Runtime">
        <section class="data-section">
          <header><h2>Slave endpoints</h2></header>
          <n-data-table
            size="small"
            :bordered="false"
            :loading="loading"
            :columns="endpointColumns"
            :data="overview?.endpoints ?? []"
            :row-key="(row: ModbusEndpoint) => row.name"
            :scroll-x="1050"
          />
        </section>

        <section class="data-section">
          <header><h2>Master sources</h2></header>
          <n-data-table
            size="small"
            :bordered="false"
            :loading="loading"
            :columns="sourceColumns"
            :data="overview?.sources ?? []"
            :row-key="(row: ModbusSource) => row.name"
            :scroll-x="900"
          />
        </section>

        <section class="data-section">
          <header><h2>Table bindings</h2></header>
          <n-data-table
            size="small"
            :bordered="false"
            :loading="loading"
            :columns="bindingColumns"
            :data="overview?.bindings ?? []"
            :row-key="(row: ModbusBinding) => row.table"
            :scroll-x="980"
          />
        </section>
      </n-tab-pane>

      <n-tab-pane name="pending" :tab="`Pending (${pendingWrites.length})`">
        <section class="data-section is-fill">
          <n-data-table
            size="small"
            :bordered="false"
            :loading="loading"
            :columns="pendingColumns"
            :data="pendingWrites"
            :row-key="(row: ModbusEndpointWrite) => row.requestId"
            :scroll-x="1500"
          />
        </section>
      </n-tab-pane>

      <n-tab-pane name="audit" tab="Audit">
        <section class="data-section is-fill">
          <n-data-table
            size="small"
            :bordered="false"
            :loading="loading"
            :columns="auditColumns"
            :data="auditEvents"
            :row-key="(row: ModbusEndpointWrite) => `${row.requestId}:${row.occurredAtUtc}:${row.eventType}`"
            :scroll-x="1550"
          />
        </section>
      </n-tab-pane>
    </n-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, h, onMounted, ref, watch } from 'vue';
import {
  NButton,
  NDataTable,
  NPopconfirm,
  NSelect,
  NTag,
  NTooltip,
  useMessage,
  type DataTableColumns,
} from 'naive-ui';
import { Check, RefreshCw, X } from 'lucide-vue-next';
import {
  approveModbusWrite,
  getModbusOverview,
  listModbusWriteAudit,
  listModbusWrites,
  modbusApiError,
  rejectModbusWrite,
  type ModbusBinding,
  type ModbusEndpoint,
  type ModbusEndpointWrite,
  type ModbusOverview,
  type ModbusSource,
} from '@/api/modbus';
import { listDatabases } from '@/api/server';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';

const auth = useAuthStore();
const connections = useConnectionsStore();
const message = useMessage();
const database = ref('');
const databases = ref<string[]>([]);
const databaseLoading = ref(false);
const loading = ref(false);
const error = ref('');
const activeTab = ref('runtime');
const overview = ref<ModbusOverview | null>(null);
const pendingWrites = ref<ModbusEndpointWrite[]>([]);
const auditEvents = ref<ModbusEndpointWrite[]>([]);
const actionRequestId = ref('');

const databaseOptions = computed(() => databases.value.map((value) => ({ label: value, value })));

const endpointColumns: DataTableColumns<ModbusEndpoint> = [
  { title: 'Endpoint', key: 'name', width: 190, fixed: 'left' },
  { title: 'Bind', key: 'bind', width: 190, render: (row) => `${formatHost(row.bindAddress)}:${row.port}` },
  { title: 'Unit', key: 'unitId', width: 70 },
  { title: 'Policy', key: 'writePolicy', width: 110, render: (row) => statusTag(row.writePolicy, row.writePolicy === 'STAGED' ? 'warning' : 'default') },
  { title: 'Health', key: 'health', width: 115, render: (row) => healthTag(row.health) },
  { title: 'Connections', key: 'maxConnections', width: 105 },
  { title: 'Allowlist', key: 'allowlist', minWidth: 220, ellipsis: { tooltip: true }, render: (row) => row.allowlist.join(', ') || 'loopback only' },
  { title: 'Error', key: 'lastErrorCode', width: 180, ellipsis: { tooltip: true }, render: (row) => row.lastErrorCode ?? '—' },
];

const sourceColumns: DataTableColumns<ModbusSource> = [
  { title: 'Source', key: 'name', width: 190, fixed: 'left' },
  { title: 'Remote', key: 'host', width: 210, render: (row) => `${formatHost(row.host)}:${row.port}` },
  { title: 'Unit', key: 'unitId', width: 70 },
  { title: 'Health', key: 'health', width: 115, render: (row) => healthTag(row.health) },
  { title: 'Configured', key: 'configuredEnabled', width: 105, render: (row) => booleanTag(row.configuredEnabled) },
  { title: 'Runtime', key: 'runtimeEnabled', width: 100, render: (row) => booleanTag(row.runtimeEnabled) },
  { title: 'Error', key: 'lastErrorCode', minWidth: 180, ellipsis: { tooltip: true }, render: (row) => row.lastErrorCode ?? '—' },
];

const bindingColumns: DataTableColumns<ModbusBinding> = [
  { title: 'Table', key: 'table', width: 190, fixed: 'left' },
  { title: 'Direction', key: 'direction', width: 100, render: (row) => statusTag(row.direction, row.direction === 'EXPOSE' ? 'info' : 'default') },
  { title: 'Target', key: 'target', width: 180 },
  { title: 'Row key', key: 'rowKey', width: 90, render: (row) => row.rowKey ?? '—' },
  { title: 'Mode', key: 'tableMode', width: 100 },
  { title: 'Approved action', key: 'approvedWriteAction', width: 150 },
  {
    title: 'Mappings',
    key: 'mappings',
    minWidth: 280,
    ellipsis: { tooltip: true },
    render: (row) => row.mappings.map((mapping) => `${mapping.column} → ${mapping.area}(${mapping.declaredAddress})`).join(' · '),
  },
];

const pendingColumns: DataTableColumns<ModbusEndpointWrite> = [
  { title: 'Received', key: 'occurredAtUtc', width: 165, fixed: 'left', render: (row) => formatTime(row.occurredAtUtc) },
  { title: 'Endpoint', key: 'endpoint', width: 160 },
  { title: 'Client', key: 'remoteEndpoint', width: 170 },
  { title: 'Target', key: 'target', width: 210, render: (row) => `${row.table ?? '—'}.${row.column ?? '—'}` },
  { title: 'Address', key: 'address', width: 175, render: (row) => `${row.area} ${row.declaredAddress}` },
  { title: 'Value', key: 'decodedValue', width: 130, ellipsis: { tooltip: true }, render: (row) => row.decodedValue ?? '—' },
  { title: 'State', key: 'state', width: 105, render: (row) => stateTag(row.state) },
  { title: 'Function', key: 'functionCode', width: 90 },
  { title: 'Action', key: 'approvedWriteAction', width: 140 },
  { title: 'Expires', key: 'expiresAtUtc', width: 165, render: (row) => row.expiresAtUtc ? formatTime(row.expiresAtUtc) : '—' },
  {
    title: '',
    key: 'actions',
    width: 86,
    fixed: 'right',
    render: (row) => h('div', { class: 'write-actions' }, [
      h(NPopconfirm, {
        positiveText: '批准',
        negativeText: '取消',
        onPositiveClick: () => decide(row, true),
      }, {
        trigger: () => h(NButton, {
          quaternary: true,
          circle: true,
          size: 'small',
          type: 'success',
          loading: actionRequestId.value === row.requestId,
          title: '批准写入',
        }, { icon: () => h(Check, { size: 16 }) }),
        default: () => `批准 ${row.table}.${row.column} = ${row.decodedValue}？`,
      }),
      h(NPopconfirm, {
        positiveText: '拒绝',
        negativeText: '取消',
        onPositiveClick: () => decide(row, false),
      }, {
        trigger: () => h(NButton, {
          quaternary: true,
          circle: true,
          size: 'small',
          type: 'error',
          loading: actionRequestId.value === row.requestId,
          title: row.state === 'applying' ? '审批恢复期间不能拒绝' : '拒绝写入',
          disabled: row.state === 'applying',
        }, { icon: () => h(X, { size: 16 }) }),
        default: () => `拒绝来自 ${row.remoteEndpoint} 的写请求？`,
      }),
    ]),
  },
];

const auditColumns: DataTableColumns<ModbusEndpointWrite> = [
  { title: 'Time', key: 'occurredAtUtc', width: 165, fixed: 'left', render: (row) => formatTime(row.occurredAtUtc) },
  { title: 'Event', key: 'eventType', width: 155 },
  { title: 'State', key: 'state', width: 115, render: (row) => stateTag(row.state) },
  { title: 'Principal', key: 'principal', width: 180, ellipsis: { tooltip: true } },
  { title: 'Endpoint', key: 'endpoint', width: 150 },
  { title: 'Client', key: 'remoteEndpoint', width: 170 },
  { title: 'Target', key: 'target', width: 210, render: (row) => `${row.table ?? '—'}.${row.column ?? '—'}` },
  { title: 'Address', key: 'address', width: 175, render: (row) => `${row.area} ${row.declaredAddress}` },
  { title: 'Value', key: 'decodedValue', width: 120, ellipsis: { tooltip: true }, render: (row) => row.decodedValue ?? '—' },
  { title: 'Error', key: 'errorCode', minWidth: 190, ellipsis: { tooltip: true }, render: (row) => row.errorCode ?? '—' },
];

async function loadDatabases(): Promise<void> {
  databaseLoading.value = true;
  try {
    const result = await listDatabases(auth.api);
    if (result.error) throw new Error(result.error.message);
    databases.value = result.databases;
    const preferred = connections.activeDatabase;
    database.value = result.databases.includes(preferred) ? preferred : (result.databases[0] ?? '');
  } catch (cause) {
    error.value = modbusApiError(cause);
  } finally {
    databaseLoading.value = false;
  }
}

async function loadAll(): Promise<void> {
  if (!database.value) {
    overview.value = null;
    pendingWrites.value = [];
    auditEvents.value = [];
    return;
  }
  loading.value = true;
  error.value = '';
  try {
    [overview.value, pendingWrites.value, auditEvents.value] = await Promise.all([
      getModbusOverview(auth.api, database.value),
      listModbusWrites(auth.api, database.value, 'pending'),
      listModbusWriteAudit(auth.api, database.value),
    ]);
  } catch (cause) {
    error.value = modbusApiError(cause);
  } finally {
    loading.value = false;
  }
}

async function decide(row: ModbusEndpointWrite, approve: boolean): Promise<void> {
  actionRequestId.value = row.requestId;
  try {
    if (approve) await approveModbusWrite(auth.api, database.value, row.requestId);
    else await rejectModbusWrite(auth.api, database.value, row.requestId);
    message.success(approve ? '写请求已批准' : '写请求已拒绝');
    await loadAll();
  } catch (cause) {
    message.error(modbusApiError(cause));
  } finally {
    actionRequestId.value = '';
  }
}

function healthTag(health: string) {
  const type = health === 'healthy' || health === 'listening'
    ? 'success'
    : health === 'degraded'
      ? 'error'
      : health === 'starting'
        ? 'warning'
        : 'default';
  return statusTag(health, type);
}

function stateTag(state: string) {
  const type = state === 'applied' || state === 'approved'
    ? 'success'
    : state === 'staged' || state === 'applying'
      ? 'warning'
      : state === 'failed' || state === 'invalidated'
        ? 'error'
        : 'default';
  return statusTag(state, type);
}

function booleanTag(value: boolean) {
  return statusTag(value ? 'yes' : 'no', value ? 'success' : 'default');
}

function statusTag(label: string, type: 'default' | 'success' | 'warning' | 'error' | 'info') {
  return h(NTag, { size: 'small', type, bordered: false }, { default: () => label });
}

function formatHost(host: string): string {
  return host.includes(':') ? `[${host}]` : host;
}

function formatTime(value: string): string {
  return new Date(value).toLocaleString('zh-CN', { hour12: false });
}

watch(database, () => { void loadAll(); });
onMounted(async () => { await loadDatabases(); });
</script>

<style scoped>
.modbus-view {
  display: flex;
  flex-direction: column;
  gap: 18px;
  min-width: 0;
}

.modbus-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  min-height: 42px;
}

.modbus-toolbar > div,
.modbus-toolbar__actions,
.runtime-state {
  display: flex;
  align-items: center;
}

.modbus-toolbar > div:first-child {
  gap: 14px;
}

.modbus-toolbar__actions {
  gap: 8px;
}

h1,
h2 {
  margin: 0;
  letter-spacing: 0;
  color: var(--sndb-ink-strong);
}

h1 {
  font-size: 20px;
  font-weight: 650;
}

h2 {
  font-size: 13px;
  font-weight: 650;
}

.runtime-state {
  gap: 7px;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.runtime-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--sndb-ink-subtle);
}

.runtime-state.is-on .runtime-dot {
  background: var(--sndb-success);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--sndb-success) 14%, transparent);
}

.status-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(130px, 1fr));
  border-block: 1px solid var(--sndb-border);
  background: #fff;
}

.status-strip > div {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  min-height: 58px;
  padding: 12px 18px;
  border-right: 1px solid var(--sndb-border);
}

.status-strip > div:last-child {
  border-right: 0;
}

.status-strip span {
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.status-strip strong {
  color: var(--sndb-ink-strong);
  font: 650 23px/1 ui-monospace, SFMono-Regular, Consolas, monospace;
}

.status-strip .is-pending strong {
  color: var(--sndb-warning);
}

.modbus-tabs {
  min-width: 0;
}

.data-section {
  margin-bottom: 20px;
  overflow: hidden;
  border: 1px solid var(--sndb-border);
  border-radius: 5px;
  background: #fff;
}

.data-section > header {
  display: flex;
  align-items: center;
  min-height: 38px;
  padding: 0 12px;
  border-bottom: 1px solid var(--sndb-border);
  background: var(--sndb-chrome);
}

.data-section :deep(.n-data-table-th) {
  font-size: 12px;
  font-weight: 650;
}

.data-section :deep(.n-data-table-td) {
  font-size: 12px;
}

.data-section :deep(.write-actions) {
  display: flex;
  justify-content: flex-end;
  gap: 2px;
}

@media (max-width: 760px) {
  .modbus-toolbar {
    align-items: flex-start;
  }

  .modbus-toolbar > div:first-child {
    align-items: flex-start;
    flex-direction: column;
    gap: 4px;
  }

  .modbus-toolbar__actions :deep(.n-select) {
    width: 150px !important;
  }

  .status-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .status-strip > div:nth-child(2) {
    border-right: 0;
  }

  .status-strip > div:nth-child(-n + 2) {
    border-bottom: 1px solid var(--sndb-border);
  }
}
</style>
