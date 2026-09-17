<template>
  <main class="kv-workbench" data-testid="workbench-kv">
    <section class="kv-toolbar">
      <div class="kv-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="success" :bordered="false">KV</n-tag>
          <n-text class="kv-toolbar__title">{{ activeKeyspace || 'No keyspace selected' }}</n-text>
          <n-tag v-if="currentPrefix" size="tiny" :bordered="false">prefix {{ currentPrefix }}</n-tag>
        </n-space>
        <n-text depth="3" class="kv-toolbar__meta">
          {{ targetDb || 'database' }} · {{ rows.length }} loaded keys · {{ checkedRowKeys.length }} selected
        </n-text>
      </div>

      <div class="kv-toolbar__actions">
        <n-select
          v-model:value="selectedKeyspace"
          size="small"
          :options="keyspaceOptions"
          :disabled="keyspaceOptions.length === 0"
          class="kv-toolbar__keyspace"
        />
        <n-input
          v-model:value="prefixInput"
          size="small"
          clearable
          placeholder="Namespace prefix"
          class="kv-toolbar__prefix"
          @keydown.enter="applyPrefix"
        />
        <n-input
          v-model:value="delimiter"
          size="small"
          maxlength="4"
          placeholder=":"
          class="kv-toolbar__delimiter"
        />
        <n-select
          v-model:value="scanLimit"
          size="small"
          :options="scanLimitOptions"
          class="kv-toolbar__limit"
        />
        <n-button size="small" secondary :disabled="!activeKeyspace" @click="applyPrefix">Scan</n-button>
        <n-button size="small" secondary :loading="loadingScan" :disabled="!activeKeyspace" @click="() => refreshAll()">
          Refresh
        </n-button>
        <n-button size="small" quaternary :disabled="rows.length === 0" @click="exportRoundTrip">
          导出 round-trip
        </n-button>
        <n-button size="small" quaternary :disabled="!activeKeyspace" @click="importFileInput?.click()">
          导入文件
        </n-button>
        <input ref="importFileInput" type="file" accept=".json,.jsonl,.ndjson,application/json,application/x-ndjson" class="kv-file-input" @change="onImportFileSelected">
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
        <n-button size="small" quaternary title="查看 KV 结果" aria-label="查看 KV 结果" :disabled="!ranOnce" @click="openResult">
          <template #icon><PanelBottom :size="16" /></template>
        </n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeView"
      :items="kvSections"
      aria-label="KV 工作区"
      @update:model-value="activeView = $event as KvView"
    />

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      abortable
      @cancel="clearPendingOperations"
      @confirm="confirmPendingOperations"
      @abort="abortPendingOperations"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="kv-alert"
      @close="errorMsg = ''"
    />

    <section v-if="activeView === 'stats'" class="kv-stats kv-stats--page">
      <article v-for="item in statItems" :key="item.label" class="kv-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section v-else class="kv-body" :class="{ 'is-batch': activeView === 'batch' }">
      <aside v-if="activeView === 'browser'" class="kv-namespace">
        <div class="kv-panel-head">
          <div>
            <n-text class="kv-panel-head__title">Keyspace tree</n-text>
            <n-text depth="3" class="kv-panel-head__meta">{{ namespaceSummary }}</n-text>
          </div>
          <n-button size="tiny" quaternary :disabled="!currentPrefix" @click="openParentPrefix">Up</n-button>
        </div>

        <div class="kv-path">
          <button type="button" @click="openPrefix('')">root</button>
          <template v-for="crumb in prefixCrumbs" :key="crumb.prefix">
            <span>/</span>
            <button type="button" @click="openPrefix(crumb.prefix)">{{ crumb.label }}</button>
          </template>
        </div>

        <div class="kv-folder-list">
          <button
            v-for="folder in namespaceFolders"
            :key="folder.prefix"
            type="button"
            class="kv-folder"
            @click="openPrefix(folder.prefix)"
          >
            <span>{{ folder.name }}</span>
            <small>{{ folder.count }} keys</small>
          </button>
          <n-empty v-if="namespaceFolders.length === 0" description="No child namespaces in loaded rows." />
        </div>
      </aside>

      <section v-if="activeView === 'browser'" class="kv-grid-panel">
        <div class="kv-panel-head kv-panel-head--grid">
          <div>
            <n-text class="kv-panel-head__title">Keys</n-text>
            <n-text depth="3" class="kv-panel-head__meta">{{ gridSummary }}</n-text>
          </div>
          <div class="kv-grid-tools">
            <n-input
              v-model:value="keyFilter"
              size="small"
              clearable
              placeholder="Filter loaded keys"
              class="kv-grid-tools__filter"
            />
            <n-button size="small" secondary :disabled="checkedRowKeys.length === 0" @click="loadSelectedKeys">
              Get selected
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="checkedRowKeys.length === 0" @click="stageRemoveSelected">
              Remove selected
            </n-button>
          </div>
        </div>

        <n-data-table
          :columns="dataColumns"
          :data="filteredRows"
          :loading="loadingScan || loading"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          :checked-row-keys="checkedRowKeys"
          size="small"
          remote
          flex-height
          class="kv-grid"
          @update:checked-row-keys="checkedRowKeys = $event"
        />

        <footer class="kv-pager">
          <span>{{ cursorText }}</span>
          <n-space size="small" align="center">
            <n-button size="small" :disabled="!hasMore || loadingScan" :loading="loadingScan" @click="loadMore">
              Load more
            </n-button>
            <n-button size="small" quaternary :disabled="rows.length === 0" @click="clearRows">Clear page</n-button>
          </n-space>
        </footer>
      </section>

      <aside class="kv-inspector">
        <div class="kv-panel-head">
          <div>
            <n-text class="kv-panel-head__title">Value inspector</n-text>
            <n-text depth="3" class="kv-panel-head__meta">{{ selectedEntry?.key ?? 'No key selected' }}</n-text>
          </div>
          <n-tag v-if="selectedEntry" size="tiny" :bordered="false">{{ selectedEntry.valueKind }}</n-tag>
        </div>

        <template v-if="activeView === 'browser' && selectedEntry">
          <div class="kv-detail-strip">
            <span>version {{ displayKvVersion(undefined, selectedEntry.version) }}</span>
            <span>{{ selectedEntry.byteLength }} bytes</span>
            <span>{{ selectedEntry.ttlLabel }}</span>
          </div>

          <n-tabs v-model:value="valueView" type="segment" size="small" class="kv-value-tabs">
            <n-tab name="text" tab="Text" />
            <n-tab name="json" tab="JSON" />
            <n-tab name="hex" tab="Hex" />
            <n-tab name="base64" tab="Base64" />
          </n-tabs>
          <pre class="kv-value-preview">{{ selectedValueText }}</pre>
        </template>
        <n-empty v-else-if="activeView === 'browser'" description="请从列表中选择一个 key。" />

        <section v-if="activeView === 'batch'" class="kv-editor">
          <n-text class="kv-editor__title">Set / edit value</n-text>
          <n-input v-model:value="editKey" size="small" placeholder="Key" />
          <div class="kv-editor__row">
            <n-select v-model:value="singleOperation" size="small" :options="singleOperationOptions" aria-label="KV operation" />
            <n-select v-if="singleOperation === 'set'" v-model:value="setCondition" size="small" :options="setConditionOptions" aria-label="Set condition" />
          </div>
          <div v-if="singleOperation !== 'get-and-delete'" class="kv-editor__row">
            <n-select v-model:value="editMode" size="small" :options="valueModeOptions" />
            <n-select v-model:value="setExpiryMode" size="small" :options="setExpiryOptions" />
          </div>
          <n-input
            v-if="singleOperation !== 'get-and-delete'"
            v-model:value="editValue"
            type="textarea"
            :autosize="{ minRows: 4, maxRows: 8 }"
            placeholder="Value"
          />
          <n-input-number
            v-if="singleOperation !== 'get-and-delete' && setExpiryMode === 'seconds'"
            v-model:value="setTtlSeconds"
            size="small"
            :min="1"
            :show-button="false"
            placeholder="Expire in seconds"
          />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" type="primary" :disabled="!activeKeyspace || confirmBusy" @click="stageSetFromEditor">
              {{ singleOperation === 'set' ? 'Stage set' : singleOperation === 'get-and-set' ? 'Stage exchange' : 'Stage get and delete' }}
            </n-button>
            <n-button size="small" secondary :disabled="!selectedEntry" @click="stageExpireSelected">
              Stage expire
            </n-button>
            <n-input-number
              v-model:value="expireSeconds"
              size="small"
              :min="1"
              :show-button="false"
              placeholder="TTL seconds"
              class="kv-editor__ttl"
            />
            <n-button size="small" secondary :disabled="!selectedEntry" @click="stagePersistSelected">
              Stage persist
            </n-button>
          </n-space>
        </section>

        <section v-if="activeView === 'batch'" class="kv-batch">
          <n-text class="kv-editor__title">Batch operations</n-text>
          <n-input
            v-model:value="batchKeysText"
            type="textarea"
            :autosize="{ minRows: 2, maxRows: 4 }"
            placeholder="Keys, one per line"
          />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" secondary @click="loadExplicitKeys">Batch get</n-button>
            <n-button size="small" tertiary type="error" @click="stageRemoveExplicitKeys">Batch remove</n-button>
          </n-space>
          <n-input
            v-model:value="batchSetText"
            type="textarea"
            :autosize="{ minRows: 3, maxRows: 6 }"
            placeholder="Batch set, one key=value per line"
          />
          <n-button size="small" secondary :disabled="!batchSetText.trim()" @click="stageBatchSet">
            Stage batch set
          </n-button>
          <div class="kv-batch__danger">
            <n-input-number
              v-model:value="prefixDeleteLimit"
              size="small"
              :min="1"
              :show-button="false"
              placeholder="Prefix delete limit"
            />
            <n-button size="small" tertiary type="error" :disabled="!currentPrefix" @click="stagePrefixDelete">
              Stage prefix delete
            </n-button>
          </div>
          <div class="kv-batch__danger">
            <n-input-number
              v-model:value="cleanExpiredLimit"
              size="small"
              :min="1"
              :show-button="false"
              placeholder="Clean expired limit"
            />
            <n-button size="small" tertiary type="error" @click="stageCleanExpired">
              Stage clean expired
            </n-button>
          </div>
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      class="kv-result"
      wrap-header
      title="KV operation result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeKeyspace || 'kv'}`"
      empty-description="Scan, read, or stage KV operations to see results."
      @clear-error="latestResult = null"
    />

    <WorkbenchHistoryDrawer
      v-model:show="historyVisible"
      :active-database="targetDb"
      @select="openHistoryEntry"
    />
  </main>
</template>

<script setup lang="ts">
import { PanelBottom } from 'lucide-vue-next';
import { computed, h, markRaw, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NTab,
  NTabs,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type DataTableRowKey,
  type SelectOption,
} from 'naive-ui';
import {
  cleanExpiredKvEntries,
  expireKvEntry,
  fetchKvStats,
  getManyKvEntries,
  getAndDeleteKvEntry,
  getAndSetKvEntry,
  persistKvEntry,
  removeKvPrefix,
  removeManyKvEntries,
  scanKvEntries,
  setManyKvEntries,
  setConditionalKvEntry,
  type KvAtomicValueResponse,
  type KvSetCondition,
  type KvEntryResponse,
  type KvStatsResponse,
  type KvValueItemResponse,
} from '@/api/kv';
import { createApiClient } from '@/api/client';
import type { SqlResultSet } from '@/api/sql';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
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
  type WriteApprovalSeverity,
} from '@/utils/writeApproval';
import { downloadText, safeFileStem } from '@/utils/resultExport';

const props = withDefaults(defineProps<{
  targetDb: string;
  keyspace: string;
  keyspaces?: string[];
  loading?: boolean;
}>(), {
  keyspaces: () => [],
  loading: false,
});

const emit = defineEmits<{
  selectKeyspace: [keyspace: string];
  refreshSchema: [];
}>();

type ValueView = 'text' | 'json' | 'hex' | 'base64';
type KvView = 'browser' | 'batch' | 'stats';
type ValueKind = 'json' | 'text' | 'binary';
type SetExpiryMode = 'persist' | 'seconds';
type SingleOperation = 'set' | 'get-and-set' | 'get-and-delete';

interface KvContext {
  revision: number;
  database: string;
  keyspace: string;
  connectionId: string;
  connectionName: string;
  baseUrl: string | undefined;
  token: string | undefined;
  sourceApi: ReturnType<typeof useAuthStore>['api'];
  api: ReturnType<typeof useAuthStore>['api'];
}

interface KvRow {
  key: string;
  value: string;
  version: number;
  expiresAtUtc: string | null;
  byteLength: number;
  valueKind: ValueKind;
  valuePreview: string;
  ttlLabel: string;
}

interface NamespaceFolder {
  name: string;
  prefix: string;
  count: number;
}

interface PendingOperation {
  id: string;
  label: string;
  detail: string;
  severity: WriteApprovalSeverity;
  command: string;
  context: KvContext;
  run: (signal: AbortSignal) => Promise<OperationOutcome>;
}

interface OperationOutcome {
  action: string;
  target: string;
  succeeded: boolean;
  affected: number;
  detail: string;
  state?: 'completed' | 'not-applied' | 'failed' | 'unknown';
  applied?: boolean;
  version?: number | null;
  versionText?: string | null;
  previous?: KvAtomicValueResponse['previous'];
  mutationVersion?: number | null;
  previousVersionText?: string | null;
  mutationVersionText?: string | null;
  errorCode?: string;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const selectedKeyspace = computed({
  get: () => props.keyspace,
  set: (value: string) => emit('selectKeyspace', value),
});

const activeKeyspace = computed(() => props.keyspace || props.keyspaces[0] || '');

const keyspaceOptions = computed<SelectOption[]>(() => {
  const names = new Set(props.keyspaces);
  if (props.keyspace) names.add(props.keyspace);
  return [...names].sort().map((name) => ({ label: name, value: name }));
});

const scanLimitOptions: SelectOption[] = [
  { label: '50 keys', value: 50 },
  { label: '100 keys', value: 100 },
  { label: '250 keys', value: 250 },
  { label: '500 keys', value: 500 },
  { label: '1000 keys', value: 1000 },
];

const valueModeOptions: SelectOption[] = [
  { label: 'Text (UTF-8)', value: 'text' },
  { label: 'JSON', value: 'json' },
  { label: 'Hex', value: 'hex' },
  { label: 'Base64', value: 'base64' },
];

const setExpiryOptions: SelectOption[] = [
  { label: 'Persistent', value: 'persist' },
  { label: 'Expire in seconds', value: 'seconds' },
];

const singleOperationOptions: SelectOption[] = [
  { label: 'Set', value: 'set' },
  { label: 'Get and set', value: 'get-and-set' },
  { label: 'Get and delete', value: 'get-and-delete' },
];
const setConditionOptions: SelectOption[] = [
  { label: 'Always', value: 0 },
  { label: 'Only if absent (NX)', value: 1 },
  { label: 'Only if present (XX)', value: 2 },
];

const rows = ref<KvRow[]>([]);
const stats = ref<KvStatsResponse | null>(null);
const currentPrefix = ref('');
const prefixInput = ref('');
const delimiter = ref(':');
const scanLimit = ref(100);
const cursor = ref<string | null>(null);
const hasMore = ref(false);
const loadingScan = ref(false);
const activeView = ref<KvView>('browser');
const kvSections: WorkbenchSectionTab[] = [
  { key: 'browser', label: '浏览器' },
  { key: 'batch', label: '批量操作' },
  { key: 'stats', label: '统计' },
];
const errorMsg = ref('');
const keyFilter = ref('');
const selectedKey = ref('');
const checkedRowKeys = ref<DataTableRowKey[]>([]);
const valueView = ref<ValueView>('text');
const editKey = ref('');
const editValue = ref('');
const editMode = ref<ValueView>('text');
const setExpiryMode = ref<SetExpiryMode>('persist');
const singleOperation = ref<SingleOperation>('set');
const setCondition = ref<KvSetCondition>(0);
const setTtlSeconds = ref<number | null>(3600);
const expireSeconds = ref<number | null>(3600);
const batchKeysText = ref('');
const batchSetText = ref('');
const importFileInput = ref<HTMLInputElement | null>(null);
const prefixDeleteLimit = ref<number | null>(1000);
const cleanExpiredLimit = ref<number | null>(1000);
const pendingOperations = ref<PendingOperation[]>([]);
const runningOperations = ref<PendingOperation[]>([]);
const confirmBusy = ref(false);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);
const maxPendingOperations = 1000;
function openResult(): void {
  window.dispatchEvent(new CustomEvent('sndb:toggle-result', { detail: { open: true } }));
}
let contextRevision = 0;
let disposed = false;
let statsRequestId = 0;
let scanRequestId = 0;
let getRequestId = 0;
let writeRequestId = 0;
let fileRequestId = 0;
let statsController: AbortController | null = null;
let scanController: AbortController | null = null;
let getController: AbortController | null = null;
let writeController: AbortController | null = null;

const selectedEntry = computed(() =>
  rows.value.find((row) => row.key === selectedKey.value) ?? null);

const filteredRows = computed(() => {
  const keyword = keyFilter.value.trim().toLowerCase();
  if (!keyword) return rows.value;
  return rows.value.filter((row) =>
    row.key.toLowerCase().includes(keyword)
    || row.valuePreview.toLowerCase().includes(keyword)
    || row.valueKind.includes(keyword));
});

const namespaceFolders = computed<NamespaceFolder[]>(() => {
  const sep = delimiter.value || ':';
  const folders = new Map<string, NamespaceFolder>();
  for (const row of rows.value) {
    if (!row.key.startsWith(currentPrefix.value)) continue;
    const rest = row.key.slice(currentPrefix.value.length);
    const index = rest.indexOf(sep);
    if (index <= -1) continue;
    const name = rest.slice(0, index);
    if (!name) continue;
    const prefix = `${currentPrefix.value}${name}${sep}`;
    const existing = folders.get(prefix);
    if (existing) {
      existing.count += 1;
    } else {
      folders.set(prefix, { name, prefix, count: 1 });
    }
  }
  return [...folders.values()].sort((a, b) => a.name.localeCompare(b.name));
});

const prefixCrumbs = computed(() => {
  const sep = delimiter.value || ':';
  const trimmed = currentPrefix.value.endsWith(sep)
    ? currentPrefix.value.slice(0, -sep.length)
    : currentPrefix.value;
  if (!trimmed) return [];
  const parts = trimmed.split(sep).filter(Boolean);
  let prefix = '';
  return parts.map((part) => {
    prefix += `${part}${sep}`;
    return { label: part, prefix };
  });
});

const namespaceSummary = computed(() =>
  currentPrefix.value
    ? `${currentPrefix.value} · ${namespaceFolders.value.length} child namespaces`
    : `${namespaceFolders.value.length} root namespaces`);

const gridSummary = computed(() =>
  `${filteredRows.value.length} visible · ${rows.value.length} loaded${hasMore.value ? ' · more available' : ''}`);

const cursorText = computed(() =>
  hasMore.value
    ? `Loaded ${rows.value.length} keys. Cursor is ready for the next page.`
    : `Loaded ${rows.value.length} keys. End of current scan window.`);

const statItems = computed(() => {
  const value = stats.value;
  return [
    { label: 'Total', value: formatStat(value?.totalKeys) },
    { label: 'Active', value: formatStat(value?.activeKeys) },
    { label: 'Expired', value: formatStat(value?.expiredKeys) },
    { label: 'Expiring', value: formatStat(value?.expiringKeys) },
    { label: 'Nearest TTL', value: formatDate(value?.nearestExpiresAtUtc) },
  ];
});

const selectedValueText = computed(() => {
  const entry = selectedEntry.value;
  if (!entry) return '';
  return formatValue(entry.value, valueView.value);
});

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  const operations = confirmBusy.value ? runningOperations.value : pendingOperations.value;
  const first = operations[0];
  if (!first) return null;
  const items: WriteApprovalItem[] = operations.map((operation) => ({
    id: operation.id,
    command: operation.command,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `kv_${first.context.revision}_${operations.map((item) => item.id).join('_')}`,
    title: 'KV operation batch',
    target: `${first.context.connectionName}: ${first.context.database}.${first.context.keyspace}`,
    items,
  });
});

const resultSummary = computed(() => {
  if (!latestResult.value) return gridSummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    const affected = latestResult.value.end.recordsAffected >= 0
      ? `affected ${latestResult.value.end.recordsAffected}`
      : `${latestResult.value.end.rowCount} rows`;
    return `${affected} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

const dataColumns = computed<DataTableColumns<KvRow>>(() => [
  { type: 'selection', width: 42 },
  {
    title: 'Key',
    key: 'key',
    minWidth: 240,
    ellipsis: { tooltip: true },
    render: (row) => h('button', {
      type: 'button',
      class: ['kv-key-button', row.key === selectedKey.value ? 'is-active' : ''],
      onClick: () => selectEntry(row.key),
    }, row.key),
  },
  {
    title: 'Type',
    key: 'valueKind',
    width: 92,
    render: (row) => h(NTag, {
      size: 'tiny',
      bordered: false,
      type: row.valueKind === 'json' ? 'success' : row.valueKind === 'text' ? 'info' : 'warning',
    }, { default: () => row.valueKind }),
  },
  {
    title: 'Bytes',
    key: 'byteLength',
    width: 88,
    render: (row) => h('code', row.byteLength.toString()),
  },
  {
    title: 'TTL',
    key: 'ttl',
    minWidth: 132,
    render: (row) => h(NTag, {
      size: 'tiny',
      bordered: false,
      type: row.expiresAtUtc ? (isExpired(row.expiresAtUtc) ? 'error' : 'warning') : 'default',
    }, { default: () => row.ttlLabel }),
  },
  {
    title: 'Preview',
    key: 'valuePreview',
    minWidth: 220,
    ellipsis: { tooltip: true },
    render: (row) => h('span', { class: 'kv-preview-cell' }, row.valuePreview),
  },
  {
    title: 'Actions',
    key: 'actions',
    width: 158,
    fixed: 'right',
    render: (row) => h(NSpace, { size: 6, wrap: false }, {
      default: () => [
        h(NButton, { size: 'tiny', secondary: true, onClick: () => copyKey(row.key) }, { default: () => 'Copy' }),
        h(NButton, { size: 'tiny', tertiary: true, type: 'error', onClick: () => stageRemoveKeys([row.key]) }, { default: () => 'Remove' }),
      ],
    }),
  },
]);

function rowKey(row: KvRow): string {
  return row.key;
}

function selectEntry(key: string): void {
  selectedKey.value = key;
}

async function refreshAll(updateResult = true): Promise<void> {
  await Promise.all([
    loadStats(),
    loadEntries(true, updateResult),
  ]);
}

async function loadStats(): Promise<void> {
  if (!props.targetDb || !activeKeyspace.value) return;
  const context = captureContext();
  const requestId = ++statsRequestId;
  statsController?.abort();
  const controller = statsController = new AbortController();
  try {
    const result = await fetchKvStats(context.api, context.database, context.keyspace, controller.signal);
    if (isCurrentContext(context) && requestId === statsRequestId && !controller.signal.aborted) stats.value = result;
  } catch (error) {
    if (isCurrentContext(context) && requestId === statsRequestId && !controller.signal.aborted) errorMsg.value = errorToMessage(error, '加载 KV 统计失败');
  } finally {
    if (statsController === controller) statsController = null;
  }
}

async function loadEntries(reset: boolean, updateResult = true): Promise<void> {
  if (!props.targetDb || !activeKeyspace.value) return;
  const started = performance.now();
  const context = captureContext();
  const requestId = ++scanRequestId;
  const prefix = currentPrefix.value;
  const limit = scanLimit.value;
  scanController?.abort();
  const controller = scanController = new AbortController();
  loadingScan.value = true;
  errorMsg.value = '';
  try {
    const response = await scanKvEntries(context.api, context.database, context.keyspace, {
      prefix,
      cursor: reset ? null : cursor.value,
      limit,
    }, controller.signal);
    if (!isCurrentContext(context) || requestId !== scanRequestId || controller.signal.aborted) return;
    const nextRows = response.entries.map(mapEntry);
    rows.value = reset ? nextRows : mergeRows(rows.value, nextRows);
    cursor.value = response.nextCursor ?? null;
    hasMore.value = response.hasMore;
    syncSelectedAfterRows();
    if (updateResult) {
      latestCommand.value = `KV SCAN ${context.keyspace} PREFIX ${JSON.stringify(prefix)} LIMIT ${limit}`;
      latestResult.value = resultFromEntries(rows.value, performanceElapsed(started));
      ranOnce.value = true;
    }
  } catch (error) {
    if (!isCurrentContext(context) || requestId !== scanRequestId || controller.signal.aborted) return;
    const msg = errorToMessage(error, '扫描 KV key 失败');
    errorMsg.value = msg;
    latestResult.value = errorResult(msg, errorCode(error));
    latestCommand.value = 'KV SCAN';
    ranOnce.value = true;
  } finally {
    if (!disposed && requestId === scanRequestId) loadingScan.value = false;
    if (scanController === controller) scanController = null;
  }
}

async function loadMore(): Promise<void> {
  if (!hasMore.value || loadingScan.value) return;
  await loadEntries(false);
}

function applyPrefix(): void {
  currentPrefix.value = prefixInput.value;
  checkedRowKeys.value = [];
  void loadEntries(true);
}

function openPrefix(prefix: string): void {
  currentPrefix.value = prefix;
  prefixInput.value = prefix;
  checkedRowKeys.value = [];
  void loadEntries(true);
}

function openParentPrefix(): void {
  const parent = parentPrefix(currentPrefix.value, delimiter.value || ':');
  openPrefix(parent);
}

function clearRows(): void {
  scanController?.abort();
  scanRequestId += 1;
  loadingScan.value = false;
  rows.value = [];
  cursor.value = null;
  hasMore.value = false;
  selectedKey.value = '';
  checkedRowKeys.value = [];
}

async function loadSelectedKeys(): Promise<void> {
  await loadKeys(checkedKeys(), 'selected get');
}

async function loadExplicitKeys(): Promise<void> {
  await loadKeys(parseKeys(batchKeysText.value), 'batch get');
}

async function loadKeys(keys: string[], action: string): Promise<void> {
  if (keys.length === 0) {
    message.warning('No keys selected.');
    return;
  }
  if (!props.targetDb || !activeKeyspace.value) return;

  const started = performance.now();
  const context = captureContext();
  const requestId = ++getRequestId;
  getController?.abort();
  const controller = getController = new AbortController();
  const command = `KV GET-MANY ${context.keyspace} ${keys.length} keys`;
  try {
    const values = await getManyKvEntries(context.api, context.database, context.keyspace, keys, controller.signal);
    if (!isCurrentContext(context) || requestId !== getRequestId || controller.signal.aborted) return;
    latestCommand.value = command;
    latestResult.value = resultFromValues(values, performanceElapsed(started));
    ranOnce.value = true;
    recordHistory('success', 'KV get-many', action, command, `${values.length} keys returned`, values.length, performanceElapsed(started), context);
  } catch (error) {
    if (!isCurrentContext(context) || requestId !== getRequestId || controller.signal.aborted) return;
    const msg = errorToMessage(error, '批量读取 KV 失败');
    errorMsg.value = msg;
    latestResult.value = errorResult(msg, errorCode(error));
    latestCommand.value = command;
    ranOnce.value = true;
    recordHistory('error', 'KV get-many', action, command, msg, 0, performanceElapsed(started), context);
  } finally {
    if (getController === controller) getController = null;
  }
}

function stageSetFromEditor(): void {
  if (confirmBusy.value || !props.targetDb || !activeKeyspace.value) return;
  const key = editKey.value.trim();
  if (!key) {
    message.error('Key is required.');
    return;
  }
  const operation = singleOperation.value;
  const context = captureContext();
  if (operation === 'get-and-delete') {
    enqueueOperation({
      id: makeOperationId('get-delete'), label: 'Get and delete', detail: key, severity: 'danger', context,
      command: `KV GET-AND-DELETE ${context.keyspace} ${JSON.stringify(key)}`,
      run: async (signal) => {
        const result = await getAndDeleteKvEntry(context.api, context.database, context.keyspace, key, signal);
        return { action: operation, target: key, succeeded: true, affected: result.previous.found ? 1 : 0,
          detail: result.previous.found ? 'Deleted' : 'Key absent', previous: result.previous, mutationVersion: result.mutationVersion,
          previousVersionText: result.previousVersionText, mutationVersionText: result.mutationVersionText };
      },
    });
    return;
  }
  const value = encodeDraftValue(editValue.value, editMode.value);
  if (!value.ok) {
    message.error(value.message);
    return;
  }
  if (setExpiryMode.value === 'seconds' && (!setTtlSeconds.value || !Number.isFinite(setTtlSeconds.value)
    || setTtlSeconds.value <= 0 || !Number.isFinite(new Date(Date.now() + setTtlSeconds.value * 1000).getTime()))) {
    message.error('TTL seconds must produce a valid future expiration.');
    return;
  }
  const expiresAtUtc = resolveSetExpiresAt();
  const request = { key, value: value.base64, expiresAtUtc };
  const condition = setCondition.value;
  const conditionLabel = condition === 1 ? 'NX' : condition === 2 ? 'XX' : 'ALWAYS';
  enqueueOperation({
    id: makeOperationId(operation), label: operation === 'set' ? 'Set' : 'Get and set',
    detail: `${key} · ${value.byteLength} bytes`, severity: 'write', context,
    command: `KV ${operation.toUpperCase()} ${context.keyspace} ${JSON.stringify(key)} entries=1${operation === 'set' ? ` ${conditionLabel}` : ''} expiresAtUtc=${expiresAtUtc ?? 'null'}`,
    run: async (signal) => {
      if (operation === 'set') {
        const result = await setConditionalKvEntry(context.api, context.database, context.keyspace, { ...request, condition }, signal);
        return { action: 'set-conditional', target: key, succeeded: true, affected: result.applied ? 1 : 0,
          detail: result.applied ? 'Applied' : 'Condition not met', state: result.applied ? 'completed' : 'not-applied',
          applied: result.applied, version: result.version, versionText: result.versionText };
      }
      const result = await getAndSetKvEntry(context.api, context.database, context.keyspace, request, signal);
      return { action: operation, target: key, succeeded: true, affected: 1, detail: 'Applied',
        previous: result.previous, mutationVersion: result.mutationVersion,
        previousVersionText: result.previousVersionText, mutationVersionText: result.mutationVersionText };
    },
  });
}

function stageBatchSet(): void {
  const parsed = parseBatchSet(batchSetText.value);
  if (!parsed.ok) {
    message.error(parsed.message);
    return;
  }
  stageSetMany(parsed.entries, null, `Set ${parsed.entries.length} keys`, 'UTF-8 key=value batch');
}

async function onImportFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  const context = captureContext();
  const requestId = ++fileRequestId;
  try {
    const text = await file.text();
    if (isCurrentContext(context) && requestId === fileRequestId && !confirmBusy.value) stageRoundTripImport(text, file.name);
  } finally {
    input.value = '';
  }
}

function exportRoundTrip(): void {
  const entries = checkedKeys().length > 0
    ? rows.value.filter((row) => checkedKeys().includes(row.key))
    : rows.value;
  if (entries.length === 0) return;
  const content = entries.map((entry) => JSON.stringify({
    format: 'sonnetdb-kv-v1',
    keyspace: activeKeyspace.value,
    key: entry.key,
    valueBase64: entry.value,
    expiresAtUtc: entry.expiresAtUtc,
  })).join('\n') + '\n';
  downloadText(
    `${safeFileStem(`${props.targetDb}_${activeKeyspace.value}`, 'kv')}.kv.jsonl`,
    content,
    'application/x-ndjson;charset=utf-8',
  );
  message.success(`已导出 ${entries.length} 个 key，可原样导回。`);
}

function stageRoundTripImport(text: string, fileName: string): void {
  const parsed = parseRoundTripEntries(text);
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return;
  }
  if (parsed.entries.length === 0) {
    errorMsg.value = '导入文件中没有 KV 记录。';
    return;
  }
  const groups = new Map<string, Array<{ key: string; value: string }>>();
  for (const entry of parsed.entries) {
    const expiry = entry.expiresAtUtc ?? '';
    const group = groups.get(expiry) ?? [];
    group.push({ key: entry.key, value: entry.valueBase64 });
    groups.set(expiry, group);
  }
  pendingOperations.value = [];
  for (const [expiry, entries] of groups) {
    stageSetMany(entries, expiry || null, `Import ${entries.length} keys`, `${fileName} · 保留 expiry`);
  }
  activeView.value = 'batch';
  errorMsg.value = '';
  message.success(`已解析 ${parsed.entries.length} 个 key，确认后写入。`);
}

function parseRoundTripEntries(text: string):
  | { ok: true; entries: Array<{ key: string; valueBase64: string; expiresAtUtc: string | null }> }
  | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: false, message: '导入文件为空。' };
  try {
    const source: unknown = trimmed.startsWith('[')
      ? JSON.parse(trimmed) as unknown
      : trimmed.split(/\r?\n/u).filter(Boolean).map((line) => JSON.parse(line) as unknown);
    const items = Array.isArray(source) ? source : [source];
    const entries = items.map((item, index) => {
      if (!item || typeof item !== 'object' || Array.isArray(item)) {
        throw new Error(`第 ${index + 1} 行必须是 JSON 对象。`);
      }
      const record = item as Record<string, unknown>;
      const key = typeof record.key === 'string' ? record.key : '';
      const valueBase64 = typeof record.valueBase64 === 'string'
        ? record.valueBase64
        : typeof record.value === 'string' ? record.value : '';
      if (!key) throw new Error(`第 ${index + 1} 行缺少 key。`);
      try { base64ToBytes(valueBase64); } catch { throw new Error(`第 ${index + 1} 行 valueBase64 无效。`); }
      const expiresAtUtc = typeof record.expiresAtUtc === 'string' && record.expiresAtUtc
        ? record.expiresAtUtc
        : null;
      if (expiresAtUtc && Number.isNaN(Date.parse(expiresAtUtc))) {
        throw new Error(`第 ${index + 1} 行 expiresAtUtc 无效。`);
      }
      return { key, valueBase64, expiresAtUtc };
    });
    return { ok: true, entries };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : 'KV 文件解析失败。' };
  }
}

function stageSetMany(entries: Array<{ key: string; value: string }>, expiresAtUtc: string | null, label: string, detail: string): void {
  const context = captureContext();
  const { database: db, keyspace } = context;
  if (!db || !keyspace) return;
  const stagedEntries = entries.map((entry) => ({ ...entry }));
  const command = [
    `KV SET-MANY ${keyspace}`,
    `entries=${stagedEntries.length}`,
    `expiresAtUtc=${expiresAtUtc ?? 'null'}`,
  ].join(' ');
  enqueueOperation({
    id: makeOperationId('set'),
    label: 'Set',
    detail,
    severity: 'write',
    command,
    context,
    run: async (signal) => {
      const response = await setManyKvEntries(context.api, db, keyspace, stagedEntries, expiresAtUtc, signal);
      return {
        action: 'set-many',
        target: label,
        succeeded: true,
        affected: Object.keys(response.versions ?? {}).length,
        detail,
      };
    },
  });
}

function stageExpireSelected(): void {
  const entry = selectedEntry.value;
  if (!entry) return;
  const context = captureContext();
  const { database: db, keyspace } = context;
  if (!db || !keyspace) return;
  const key = entry.key;
  const seconds = expireSeconds.value;
  if (!seconds || seconds <= 0) {
    message.error('TTL seconds must be greater than zero.');
    return;
  }
  const expiresAtUtc = new Date(Date.now() + seconds * 1000).toISOString();
  enqueueOperation({
    id: makeOperationId('expire'),
    label: 'Expire',
    detail: `${key} · ${seconds}s`,
    severity: 'write',
    command: `KV EXPIRE ${keyspace} ${JSON.stringify(key)} ${expiresAtUtc}`,
    context,
    run: async (signal) => {
      const response = await expireKvEntry(context.api, db, keyspace, key, expiresAtUtc, signal);
      return {
        action: 'expire',
        target: key,
        succeeded: response.succeeded,
        affected: response.succeeded ? 1 : 0,
        detail: expiresAtUtc,
      };
    },
  });
}

function stagePersistSelected(): void {
  const entry = selectedEntry.value;
  if (!entry) return;
  const context = captureContext();
  const { database: db, keyspace } = context;
  if (!db || !keyspace) return;
  const key = entry.key;
  enqueueOperation({
    id: makeOperationId('persist'),
    label: 'Persist',
    detail: key,
    severity: 'write',
    command: `KV PERSIST ${keyspace} ${JSON.stringify(key)}`,
    context,
    run: async (signal) => {
      const response = await persistKvEntry(context.api, db, keyspace, key, signal);
      return {
        action: 'persist',
        target: key,
        succeeded: response.succeeded,
        affected: response.succeeded ? 1 : 0,
        detail: response.succeeded ? 'TTL removed' : 'key not found',
      };
    },
  });
}

function stageRemoveSelected(): void {
  stageRemoveKeys(checkedKeys());
}

function stageRemoveExplicitKeys(): void {
  stageRemoveKeys(parseKeys(batchKeysText.value));
}

function stageRemoveKeys(keys: string[]): void {
  if (keys.length === 0) {
    message.warning('No keys selected.');
    return;
  }
  const context = captureContext();
  const { database: db, keyspace } = context;
  if (!db || !keyspace) return;
  const stagedKeys = [...keys];
  enqueueOperation({
    id: makeOperationId('remove'),
    label: 'Remove',
    detail: `${stagedKeys.length} keys`,
    severity: 'danger',
    command: `KV REMOVE-MANY ${keyspace} ${stagedKeys.map((key) => JSON.stringify(key)).join(', ')}`,
    context,
    run: async (signal) => {
      const response = await removeManyKvEntries(context.api, db, keyspace, stagedKeys, signal);
      return {
        action: 'remove-many',
        target: `${stagedKeys.length} keys`,
        succeeded: true,
        affected: response.removed,
        detail: `removed ${response.removed}`,
      };
    },
  });
}

function stagePrefixDelete(): void {
  if (!currentPrefix.value) {
    message.error('Prefix delete requires a non-empty namespace prefix.');
    return;
  }
  const context = captureContext();
  const { database: db, keyspace } = context;
  if (!db || !keyspace) return;
  const prefix = currentPrefix.value;
  const limit = prefixDeleteLimit.value && prefixDeleteLimit.value > 0 ? prefixDeleteLimit.value : null;
  enqueueOperation({
    id: makeOperationId('prefix'),
    label: 'Prefix delete',
    detail: `${prefix}${limit ? ` · limit ${limit}` : ''}`,
    severity: 'danger',
    command: `KV REMOVE-PREFIX ${keyspace} ${JSON.stringify(prefix)} LIMIT ${limit ?? 'none'}`,
    context,
    run: async (signal) => {
      const response = await removeKvPrefix(context.api, db, keyspace, prefix, limit, signal);
      return {
        action: 'remove-prefix',
        target: prefix,
        succeeded: true,
        affected: response.removed,
        detail: `removed ${response.removed}`,
      };
    },
  });
}

function stageCleanExpired(): void {
  const context = captureContext();
  const { database: db, keyspace } = context;
  if (!db || !keyspace) return;
  const limit = cleanExpiredLimit.value && cleanExpiredLimit.value > 0 ? cleanExpiredLimit.value : null;
  enqueueOperation({
    id: makeOperationId('clean'),
    label: 'Clean expired',
    detail: limit ? `limit ${limit}` : 'no limit',
    severity: 'danger',
    command: `KV CLEAN-EXPIRED ${keyspace} LIMIT ${limit ?? 'none'}`,
    context,
    run: async (signal) => {
      const response = await cleanExpiredKvEntries(context.api, db, keyspace, limit, signal);
      return {
        action: 'clean-expired',
        target: keyspace,
        succeeded: true,
        affected: response.removed,
        detail: `removed ${response.removed}`,
      };
    },
  });
}

async function confirmPendingOperations(): Promise<void> {
  if (confirmBusy.value || pendingOperations.value.length === 0) return;
  const operations = [...pendingOperations.value];
  const context = operations[0]!.context;
  if (!operations.every((operation) => isCurrentContext(operation.context))) {
    pendingOperations.value = [];
    return;
  }
  const requestId = ++writeRequestId;
  const controller = writeController = new AbortController();
  statsController?.abort();
  scanController?.abort();
  getController?.abort();
  loadingScan.value = false;
  // Once dispatched, a write with a lost response must never re-enter the queue.
  pendingOperations.value = [];
  runningOperations.value = operations;
  confirmBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const command = operations.map((operation) => operation.command).join('\n');
  const outcomes: OperationOutcome[] = [];
  const deadline = Date.now() + 120_000;
  const timeout = setTimeout(() => controller.abort(), 120_000);
  try {
    for (let index = 0; index < Math.min(operations.length, maxPendingOperations) && Date.now() < deadline; index += 1) {
      const operation = operations[index]!;
      if (controller.signal.aborted || !isCurrentContext(operation.context)) break;
      try {
        outcomes.push(await operation.run(controller.signal));
      } catch (error) {
        const status = (error as { response?: { status?: number } } | null)?.response?.status;
        const knownFailure = typeof status === 'number' && status >= 400 && status < 500 && status !== 408;
        outcomes.push({ action: operation.label, target: operation.detail, succeeded: false, affected: 0,
          state: knownFailure ? 'failed' : 'unknown', errorCode: errorCode(error),
          detail: knownFailure ? errorToMessage(error, '写入被拒绝') : '写入结果未知，请核对服务端状态后重新暂存；未自动重试。' });
        break;
      }
    }
    const elapsed = performanceElapsed(started);
    const affected = outcomes.reduce((sum, item) => sum + item.affected, 0);
    const failure = outcomes.find((outcome) => outcome.state === 'failed' || outcome.state === 'unknown');
    const stopped = controller.signal.aborted || outcomes.length < operations.length;
    const status = failure && !controller.signal.aborted ? 'error' : stopped ? 'cancelled' : 'success';
    const versions = outcomes.map((outcome, index) => {
      const fields = [
        ['version', displayKvVersion(outcome.versionText, outcome.version)],
        ['previousVersion', displayKvVersion(outcome.previousVersionText, outcome.previous?.version)],
        ['mutationVersion', displayKvVersion(outcome.mutationVersionText, outcome.mutationVersion)],
      ].filter(([, value]) => value !== null);
      return fields.length ? `#${index + 1} ${fields.map(([name, value]) => `${name}=${value}`).join(', ')}` : '';
    }).filter(Boolean).join('; ');
    const summary = `${outcomes.length}/${operations.length} actions · affected ${affected}${failure ? ` · ${failure.errorCode}: ${failure.detail}` : stopped ? ' · 后续操作未执行' : ''}${versions ? ` · ${versions}` : ''}`;
    recordHistory(status, 'KV operation batch', operations.map((operation) => operation.label).join(', '), command, summary, affected, elapsed, context);
    if (!isCurrentContext(context) || requestId !== writeRequestId) return;
    latestCommand.value = command;
    latestResult.value = resultFromOutcomes(outcomes, elapsed);
    ranOnce.value = true;
    errorMsg.value = failure?.detail ?? (stopped ? '后续操作未执行，未重试已派发写入。' : '');
    checkedRowKeys.value = [];
    if (!failure && !stopped) {
      batchSetText.value = '';
      message.success(`Completed ${outcomes.length} KV actions; affected ${affected}.`);
      void refreshAll(false);
      emit('refreshSchema');
    }
  } finally {
    clearTimeout(timeout);
    if (writeController === controller) writeController = null;
    if (!disposed && requestId === writeRequestId) {
      confirmBusy.value = false;
      runningOperations.value = [];
    }
  }
}

function clearPendingOperations(): void {
  if (confirmBusy.value) return;
  pendingOperations.value = [];
}

function abortPendingOperations(): void {
  writeController?.abort();
}

function enqueueOperation(operation: PendingOperation): void {
  if (confirmBusy.value || !isCurrentContext(operation.context)) return;
  if (pendingOperations.value.some((pending) => !isCurrentContext(pending.context))) pendingOperations.value = [];
  if (pendingOperations.value.length >= maxPendingOperations) {
    message.error(`At most ${maxPendingOperations} staged actions are allowed.`);
    return;
  }
  pendingOperations.value.push(operation);
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function checkedKeys(): string[] {
  return checkedRowKeys.value.map(String).filter(Boolean);
}

function syncSelectedAfterRows(): void {
  if (selectedKey.value && rows.value.some((row) => row.key === selectedKey.value)) return;
  selectedKey.value = rows.value[0]?.key ?? '';
}

function mapEntry(entry: KvEntryResponse): KvRow {
  const bytes = base64ToBytes(entry.value);
  const kind = classifyValue(entry.value);
  return {
    key: entry.key,
    value: entry.value,
    version: entry.version,
    expiresAtUtc: entry.expiresAtUtc ?? null,
    byteLength: bytes.length,
    valueKind: kind,
    valuePreview: previewValue(entry.value, kind),
    ttlLabel: ttlLabel(entry.expiresAtUtc ?? null),
  };
}

function mergeRows(existing: KvRow[], incoming: KvRow[]): KvRow[] {
  const map = new Map(existing.map((row) => [row.key, row]));
  for (const row of incoming) {
    map.set(row.key, row);
  }
  return [...map.values()].sort((a, b) => a.key.localeCompare(b.key));
}

function resultFromEntries(entries: KvRow[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['key', 'type', 'version', 'ttl', 'bytes', 'preview'],
    rows: entries.map((entry) => [
      entry.key,
      entry.valueKind,
      displayKvVersion(undefined, entry.version),
      entry.ttlLabel,
      entry.byteLength,
      entry.valuePreview,
    ]),
    end: {
      type: 'end',
      rowCount: entries.length,
      recordsAffected: -1,
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function resultFromValues(values: KvValueItemResponse[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['key', 'found', 'version', 'ttl', 'bytes', 'preview'],
    rows: values.map((item) => {
      const value = item.value ?? '';
      const bytes = item.found && value ? base64ToBytes(value).length : 0;
      const kind = item.found && value ? classifyValue(value) : 'text';
      return [
        item.key,
        item.found,
        displayKvVersion(undefined, item.version),
        ttlLabel(item.expiresAtUtc ?? null),
        bytes,
        item.found && value ? previewValue(value, kind) : '',
      ];
    }),
    end: {
      type: 'end',
      rowCount: values.length,
      recordsAffected: -1,
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function resultFromOutcomes(outcomes: OperationOutcome[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['action', 'target', 'state', 'succeeded', 'affected', 'applied', 'version', 'previousFound', 'previousValueBase64', 'previousVersion', 'previousExpiresAtUtc', 'mutationVersion', 'errorCode', 'detail'],
    rows: outcomes.map((item) => [item.action, item.target, item.state ?? 'completed', item.succeeded, item.affected,
      item.applied ?? null, displayKvVersion(item.versionText, item.version), item.previous?.found ?? null,
      item.previous?.found ? item.previous.value ?? '' : null, displayKvVersion(item.previousVersionText, item.previous?.version),
      item.previous?.expiresAtUtc ?? null, displayKvVersion(item.mutationVersionText, item.mutationVersion), item.errorCode ?? null, item.detail]),
    end: {
      type: 'end',
      rowCount: outcomes.length,
      recordsAffected: outcomes.reduce((sum, item) => sum + item.affected, 0),
      elapsedMs,
    },
    error: null,
    hasColumns: true,
  };
}

function displayKvVersion(text: string | null | undefined, numeric: number | null | undefined): string | number | null {
  if (text != null) {
    return text.length <= 20 && /^(0|[1-9][0-9]*)$/.test(text) ? text : 'unavailable (invalid version text)';
  }
  if (numeric == null) return null;
  return Number.isSafeInteger(numeric) && numeric >= 0 ? numeric : 'unavailable (unsafe numeric version)';
}

function errorResult(messageText: string, code = 'kv_error'): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: { type: 'error', code, message: messageText },
    hasColumns: false,
  };
}

function parseKeys(text: string): string[] {
  return [...new Set(text.split(/\r?\n/g).map((line) => line.trim()).filter(Boolean))];
}

function parseBatchSet(text: string):
  | { ok: true; entries: Array<{ key: string; value: string }> }
  | { ok: false; message: string } {
  const entries: Array<{ key: string; value: string }> = [];
  const lines = text.split(/\r?\n/g).map((line) => line.trim()).filter(Boolean);
  if (lines.length === 0) return { ok: false, message: 'Batch set requires at least one key=value line.' };
  for (const line of lines) {
    const index = line.indexOf('=');
    if (index <= 0) {
      return { ok: false, message: `Invalid batch set line: ${line}` };
    }
    const key = line.slice(0, index).trim();
    const value = line.slice(index + 1);
    if (!key) return { ok: false, message: `Invalid empty key in line: ${line}` };
    entries.push({ key, value: utf8ToBase64(value) });
  }
  return { ok: true, entries };
}

function encodeDraftValue(text: string, mode: ValueView):
  | { ok: true; base64: string; byteLength: number }
  | { ok: false; message: string } {
  try {
    if (mode === 'hex') {
      const bytes = hexToBytes(text);
      return { ok: true, base64: bytesToBase64(bytes), byteLength: bytes.length };
    }
    if (mode === 'base64') {
      const bytes = base64ToBytes(text.trim());
      return { ok: true, base64: bytesToBase64(bytes), byteLength: bytes.length };
    }
    if (mode === 'json') {
      JSON.parse(text);
    }
    const bytes = new TextEncoder().encode(text);
    return { ok: true, base64: bytesToBase64(bytes), byteLength: bytes.length };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : 'Invalid value.' };
  }
}

function resolveSetExpiresAt(): string | null {
  if (setExpiryMode.value === 'persist') return null;
  if (setExpiryMode.value === 'seconds') {
    const seconds = setTtlSeconds.value;
    if (!seconds || seconds <= 0) return null;
    return new Date(Date.now() + seconds * 1000).toISOString();
  }
  return null;
}

function classifyValue(base64: string): ValueKind {
  const text = tryDecodeUtf8(base64);
  if (!text.ok) return 'binary';
  const trimmed = text.text.trim();
  if ((trimmed.startsWith('{') && trimmed.endsWith('}')) || (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
    try {
      JSON.parse(trimmed);
      return 'json';
    } catch {
      return 'text';
    }
  }
  return isMostlyPrintable(text.text) ? 'text' : 'binary';
}

function previewValue(base64: string, kind: ValueKind): string {
  if (kind === 'binary') {
    return toHex(base64ToBytes(base64)).slice(0, 96);
  }
  const decoded = tryDecodeUtf8(base64);
  if (!decoded.ok) return '';
  const text = decoded.text.replace(/\s+/g, ' ').trim();
  return text.length > 160 ? `${text.slice(0, 157)}...` : text;
}

function formatValue(base64: string, mode: ValueView): string {
  if (mode === 'base64') return base64;
  const bytes = base64ToBytes(base64);
  if (mode === 'hex') return toHex(bytes);
  const decoded = tryDecodeUtf8(base64);
  if (!decoded.ok) return `Binary payload (${bytes.length} bytes). Use Hex or Base64 view.`;
  if (mode === 'json') {
    try {
      return JSON.stringify(JSON.parse(decoded.text), null, 2);
    } catch {
      return decoded.text;
    }
  }
  return decoded.text;
}

function parentPrefix(prefix: string, sep: string): string {
  if (!prefix) return '';
  const trimmed = prefix.endsWith(sep) ? prefix.slice(0, -sep.length) : prefix;
  const index = trimmed.lastIndexOf(sep);
  return index >= 0 ? `${trimmed.slice(0, index)}${sep}` : '';
}

function ttlLabel(expiresAtUtc: string | null): string {
  if (!expiresAtUtc) return 'persistent';
  const expires = Date.parse(expiresAtUtc);
  if (Number.isNaN(expires)) return 'ttl unknown';
  const delta = expires - Date.now();
  if (delta <= 0) return 'expired';
  return formatDuration(delta);
}

function isExpired(expiresAtUtc: string): boolean {
  const expires = Date.parse(expiresAtUtc);
  return Number.isFinite(expires) && expires <= Date.now();
}

function formatDuration(ms: number): string {
  const seconds = Math.ceil(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.ceil(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.ceil(minutes / 60);
  if (hours < 48) return `${hours}h`;
  return `${Math.ceil(hours / 24)}d`;
}

function formatDate(value?: string | null): string {
  if (!value) return '-';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString();
}

function formatStat(value?: number): string {
  return typeof value === 'number' ? value.toLocaleString() : '-';
}

function tryDecodeUtf8(base64: string): { ok: true; text: string } | { ok: false } {
  try {
    return { ok: true, text: new TextDecoder('utf-8', { fatal: true }).decode(base64ToBytes(base64)) };
  } catch {
    return { ok: false };
  }
}

function isMostlyPrintable(text: string): boolean {
  if (!text) return true;
  let printable = 0;
  for (const ch of text) {
    const code = ch.charCodeAt(0);
    if (code === 9 || code === 10 || code === 13 || code >= 32) printable += 1;
  }
  return printable / text.length > 0.9;
}

function utf8ToBase64(text: string): string {
  return bytesToBase64(new TextEncoder().encode(text));
}

function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    const chunk = bytes.subarray(i, i + chunkSize);
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}

function hexToBytes(text: string): Uint8Array {
  const normalized = text.replace(/\s+/g, '');
  if (normalized.length % 2 !== 0 || /[^0-9a-f]/i.test(normalized)) {
    throw new Error('Hex value must contain an even number of hexadecimal characters.');
  }
  const bytes = new Uint8Array(normalized.length / 2);
  for (let i = 0; i < normalized.length; i += 2) {
    bytes[i / 2] = Number.parseInt(normalized.slice(i, i + 2), 16);
  }
  return bytes;
}

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join(' ');
}

function makeOperationId(prefix: string): string {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function performanceElapsed(started: number): number {
  return started > 0 ? performance.now() - started : 0;
}

function errorToMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const response = (error as { response?: { data?: unknown; status?: number } }).response;
    if (response?.data && typeof response.data === 'object') {
      const data = response.data as Record<string, unknown>;
      if (typeof data.message === 'string') return data.message;
      if (typeof data.error === 'string') return data.error;
    }
    if (typeof (error as { message?: unknown }).message === 'string') {
      return (error as { message: string }).message;
    }
  }
  return fallback;
}

function errorCode(error: unknown): string {
  const value = error as { code?: unknown; response?: { data?: { code?: unknown; error?: { code?: unknown } } } } | null;
  const code = value?.response?.data?.code ?? value?.response?.data?.error?.code ?? value?.code;
  return typeof code === 'string' ? code : 'kv_error';
}

async function copyKey(key: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(key);
    message.success('Key copied');
  } catch {
    message.warning(key);
  }
}

function recordHistory(
  status: 'success' | 'error' | 'cancelled',
  title: string,
  action: string,
  command: string,
  summary: string,
  recordsAffected: number,
  elapsedMs: number,
  context: KvContext,
): void {
  history.record({
    kind: 'operation',
    status,
    title,
    target: context.keyspace,
    database: context.database,
    connectionId: context.connectionId,
    connectionName: context.connectionName,
    model: 'kv',
    action,
    command,
    summary,
    recordsAffected,
    elapsedMs,
  });
}

function captureContext(): KvContext {
  const token = auth.state?.token;
  const baseUrl = auth.api.defaults.baseURL;
  const api = markRaw(createApiClient(() => token ?? null));
  api.defaults.baseURL = baseUrl;
  return { revision: contextRevision, database: props.targetDb, keyspace: activeKeyspace.value,
    connectionId: connections.activeProfileId, connectionName: connections.activeProfile.name,
    baseUrl, token, sourceApi: auth.api, api };
}

function isCurrentContext(context: KvContext): boolean {
  return !disposed && context.revision === contextRevision && context.database === props.targetDb
    && context.keyspace === activeKeyspace.value && context.connectionId === connections.activeProfileId
    && context.sourceApi === auth.api && context.baseUrl === auth.api.defaults.baseURL && context.token === auth.state?.token;
}

function cancelRequests(): void {
  statsController?.abort();
  scanController?.abort();
  getController?.abort();
  writeController?.abort();
}

watch(() => [props.targetDb, activeKeyspace.value, connections.activeProfileId, connections.activeBaseUrl, auth.api, auth.state?.token], () => {
  contextRevision += 1;
  cancelRequests();
  clearRows();
  stats.value = null;
  latestResult.value = null;
  latestCommand.value = '';
  ranOnce.value = false;
  errorMsg.value = '';
  currentPrefix.value = '';
  prefixInput.value = '';
  pendingOperations.value = [];
  runningOperations.value = [];
  confirmBusy.value = false;
  batchKeysText.value = '';
  batchSetText.value = '';
  editKey.value = '';
  editValue.value = '';
  setExpiryMode.value = 'persist';
  const revision = contextRevision;
  void nextTick().then(() => {
    if (!disposed && revision === contextRevision) return refreshAll();
  });
}, { immediate: true, flush: 'sync' });

watch(selectedEntry, (entry) => {
  if (activeView.value === 'batch') return;
  editKey.value = entry?.key ?? '';
  const nextMode: ValueView = entry?.valueKind === 'json'
    ? 'json'
    : entry?.valueKind === 'binary'
      ? 'base64'
      : 'text';
  editMode.value = nextMode;
  editValue.value = entry ? formatValue(entry.value, nextMode) : '';
  setExpiryMode.value = 'persist';
});

watch(editMode, (mode) => {
  const entry = selectedEntry.value;
  if (!entry || editKey.value !== entry.key) return;
  editValue.value = formatValue(entry.value, mode);
});

onBeforeUnmount(() => {
  disposed = true;
  contextRevision += 1;
  cancelRequests();
});
</script>

<style scoped>
.kv-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.kv-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfefa;
}

.kv-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.kv-toolbar__title {
  overflow-wrap: anywhere;
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.kv-toolbar__meta,
.kv-panel-head__meta {
  font-size: 12px;
}

.kv-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.kv-toolbar__keyspace {
  width: 160px;
}

.kv-toolbar__prefix {
  width: 220px;
}

.kv-toolbar__delimiter {
  width: 58px;
}

.kv-toolbar__limit {
  width: 104px;
}

.kv-file-input {
  display: none;
}

.kv-alert {
  margin: 10px 12px 0;
}

.kv-stats {
  display: grid;
  grid-template-columns: repeat(5, minmax(120px, 1fr));
  gap: 0;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.kv-stats--page {
  align-content: start;
  flex: 1;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  padding: 20px;
  background: var(--sndb-surface);
}

.kv-stats--page .kv-stat {
  min-height: 92px;
  justify-content: center;
  border: 1px solid var(--sndb-border);
  background: #fff;
}

.kv-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.kv-stat span {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.kv-stat strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 16px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kv-body {
  display: grid;
  flex: 1;
  min-height: 360px;
  grid-template-columns: 230px minmax(360px, 1fr) 340px;
  min-width: 0;
  overflow: hidden;
}

.kv-body.is-batch {
  grid-template-columns: minmax(0, 720px);
  justify-content: center;
  padding: 20px;
  overflow: auto;
  background: var(--sndb-surface);
}

.kv-body.is-batch .kv-inspector {
  border: 1px solid var(--sndb-border);
  border-radius: var(--sndb-radius);
  background: #fff;
}

.kv-namespace,
.kv-grid-panel,
.kv-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.kv-namespace {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.kv-grid-panel {
  background: #fff;
}

.kv-inspector {
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fcfdf8;
}

.kv-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.kv-panel-head--grid {
  align-items: center;
}

.kv-panel-head__title,
.kv-editor__title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.kv-grid-tools {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.kv-grid-tools__filter {
  width: 190px;
}

.kv-path {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
  padding: 8px 10px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.kv-path button,
.kv-folder,
.kv-key-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.kv-path button {
  padding: 2px 4px;
  border-radius: 4px;
  color: var(--sndb-brand);
  font-weight: 700;
}

.kv-path button:hover,
.kv-folder:hover,
.kv-key-button:hover {
  background: rgba(24, 160, 88, 0.08);
}

.kv-folder-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 8px;
}

.kv-folder {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-radius: 6px;
  text-align: left;
}

.kv-folder span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kv-folder small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.kv-grid {
  flex: 1;
  min-height: 0;
}

.kv-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.kv-key-button {
  max-width: 100%;
  overflow: hidden;
  padding: 2px 5px;
  border-radius: 4px;
  color: var(--sndb-ink-strong);
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  font-weight: 700;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kv-key-button.is-active {
  background: rgba(24, 160, 88, 0.14);
}

.kv-preview-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: #345;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.kv-pager {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

.kv-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 9px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.kv-detail-strip span {
  padding: 2px 7px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.07);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.kv-value-tabs {
  flex: 0 0 auto;
  padding: 8px 12px 0;
}

.kv-value-preview {
  flex: 0 0 170px;
  min-height: 120px;
  max-height: 220px;
  margin: 8px 12px;
  overflow: auto;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.kv-editor,
.kv-batch {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

.kv-editor__row,
.kv-batch__danger {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 8px;
  align-items: center;
}

.kv-editor__ttl {
  width: 120px;
}

.kv-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1320px) {
  .kv-body {
    grid-template-columns: 220px minmax(360px, 1fr);
  }

  .kv-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .kv-toolbar,
  .kv-panel-head--grid,
  .kv-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .kv-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .kv-body.is-batch {
    padding: 12px;
  }

  .kv-namespace,
  .kv-inspector {
    border-right: 0;
    border-left: 0;
  }

  .kv-stats {
    grid-template-columns: repeat(2, minmax(120px, 1fr));
  }

  .kv-toolbar__keyspace,
  .kv-toolbar__prefix,
  .kv-toolbar__limit,
  .kv-grid-tools__filter {
    width: 100%;
  }
}

@media (max-width: 600px) {
  .kv-editor__row,
  .kv-batch__danger {
    grid-template-columns: minmax(0, 1fr);
  }

  .kv-panel-head__meta {
    overflow-wrap: anywhere;
  }
}
</style>
