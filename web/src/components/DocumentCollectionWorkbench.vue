<template>
  <main class="document-workbench">
    <section class="document-toolbar">
      <div class="document-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="info" :bordered="false">Document</n-tag>
          <n-text class="document-toolbar__title">{{ activeCollectionName || 'No collection selected' }}</n-text>
          <n-tag v-if="activeCollection?.validator" size="tiny" :bordered="false">
            validator {{ activeCollection.validator.validationAction ?? 'error' }}
          </n-tag>
        </n-space>
        <n-text depth="3" class="document-toolbar__meta">
          {{ targetDb || 'database' }} · {{ rows.length }} loaded docs · {{ checkedRowKeys.length }} selected
        </n-text>
      </div>

      <div class="document-toolbar__actions">
        <n-select
          v-model:value="selectedCollectionName"
          size="small"
          :options="collectionOptions"
          :disabled="collectionOptions.length === 0"
          class="document-toolbar__collection"
        />
        <n-input-number
          v-model:value="limit"
          size="small"
          :min="1"
          :max="1000"
          :show-button="false"
          placeholder="Limit"
          class="document-toolbar__limit"
        />
        <n-button size="small" secondary :disabled="!activeCollectionName" :loading="queryBusy" @click="runFind(false)">
          Browse
        </n-button>
        <n-button size="small" secondary :loading="props.loading || queryBusy" @click="$emit('refreshSchema')">
          Refresh
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="clearPendingOperations"
      @confirm="confirmPendingOperations"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="document-alert"
      @close="errorMsg = ''"
    />

    <section class="document-stats">
      <article v-for="item in statItems" :key="item.label" class="document-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section class="document-body">
      <aside class="document-collections">
        <div class="document-panel-head">
          <div>
            <n-text class="document-panel-head__title">Collections</n-text>
            <n-text depth="3" class="document-panel-head__meta">{{ filteredCollections.length }} visible · {{ collections.length }} total</n-text>
          </div>
        </div>

        <div class="document-create">
          <n-input v-model:value="collectionFilter" size="small" clearable placeholder="Filter collections" />
          <n-input v-model:value="newCollectionName" size="small" placeholder="New collection" />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" type="primary" :disabled="!newCollectionName.trim()" @click="stageCreateCollection">
              Stage create
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="!activeCollectionName" @click="stageDropCollection">
              Stage drop
            </n-button>
          </n-space>
        </div>

        <div class="document-collection-list">
          <button
            v-for="item in filteredCollections"
            :key="item.name"
            type="button"
            class="document-collection-card"
            :class="{ 'is-active': item.name === activeCollectionName }"
            @click="selectCollection(item.name)"
          >
            <span>{{ item.name }}</span>
            <small>{{ collectionMeta(item) }}</small>
          </button>
          <n-empty v-if="filteredCollections.length === 0" description="No document collections." />
        </div>
      </aside>

      <section class="document-query-panel">
        <div class="document-panel-head document-panel-head--grid">
          <div>
            <n-text class="document-panel-head__title">Document Explorer</n-text>
            <n-text depth="3" class="document-panel-head__meta">{{ querySummary }}</n-text>
          </div>
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" secondary :disabled="!activeCollectionName" :loading="queryBusy" @click="runFind(false)">
              Run find
            </n-button>
            <n-button size="small" secondary :disabled="!hasMore || queryBusy" :loading="queryBusy" @click="runFind(true)">
              Next page
            </n-button>
            <n-button size="small" quaternary :disabled="rows.length === 0" @click="exportLoadedJsonl">
              Export JSONL
            </n-button>
          </n-space>
        </div>

        <section class="document-query-editor">
          <n-tabs v-model:value="queryTab" type="segment" size="small">
            <n-tab name="find" tab="Find" />
            <n-tab name="aggregate" tab="Aggregate" />
            <n-tab name="distinct" tab="Distinct" />
          </n-tabs>

          <template v-if="queryTab === 'find'">
            <div class="document-editor-grid">
              <n-input
                v-model:value="idsText"
                type="textarea"
                :autosize="{ minRows: 3, maxRows: 6 }"
                placeholder="IDs, one per line"
              />
              <n-input
                v-model:value="filterText"
                type="textarea"
                :autosize="{ minRows: 3, maxRows: 6 }"
                placeholder="{ &quot;path&quot;: &quot;$.site&quot;, &quot;op&quot;: &quot;eq&quot;, &quot;value&quot;: &quot;north&quot; }"
              />
            </div>
            <div class="document-editor-grid">
              <n-input
                v-model:value="projectionText"
                type="textarea"
                :autosize="{ minRows: 3, maxRows: 6 }"
                placeholder="Projection JSON array or lines: name=$.path"
              />
              <n-input
                v-model:value="sortText"
                type="textarea"
                :autosize="{ minRows: 3, maxRows: 6 }"
                placeholder="Sort JSON array or lines: $.score desc"
              />
            </div>
            <div class="document-query-row">
              <n-input-number v-model:value="skip" size="small" :min="0" :show-button="false" placeholder="Skip" />
              <n-input v-model:value="continuationToken" size="small" clearable placeholder="Continuation token" />
              <n-button size="small" secondary :loading="countBusy" :disabled="!activeCollectionName" @click="runCount">
                Count
              </n-button>
            </div>
          </template>

          <template v-else-if="queryTab === 'aggregate'">
            <n-input
              v-model:value="aggregateText"
              type="textarea"
              :autosize="{ minRows: 7, maxRows: 12 }"
              placeholder="[{ &quot;$match&quot;: { &quot;path&quot;: &quot;$.score&quot;, &quot;op&quot;: &quot;gte&quot;, &quot;value&quot;: 5 } }]"
            />
            <n-button size="small" secondary :disabled="!activeCollectionName" :loading="queryBusy" @click="runAggregate">
              Run aggregate
            </n-button>
          </template>

          <template v-else>
            <div class="document-query-row document-query-row--distinct">
              <n-input v-model:value="distinctPath" size="small" placeholder="$.site" />
              <n-input-number v-model:value="distinctLimit" size="small" :min="1" :show-button="false" placeholder="Limit" />
              <n-button size="small" secondary :disabled="!activeCollectionName || !distinctPath.trim()" :loading="queryBusy" @click="runDistinct">
                Run distinct
              </n-button>
            </div>
          </template>
        </section>

        <n-data-table
          :columns="documentColumns"
          :data="filteredRows"
          :loading="queryBusy || props.loading"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          :checked-row-keys="checkedRowKeys"
          size="small"
          remote
          flex-height
          class="document-grid"
          @update:checked-row-keys="checkedRowKeys = $event"
        />

        <footer class="document-pager">
          <span>{{ pagerText }}</span>
          <n-space size="small" align="center">
            <n-input v-model:value="gridFilter" size="small" clearable placeholder="Filter loaded rows" class="document-grid-filter" />
            <n-button size="small" tertiary type="error" :disabled="checkedRowKeys.length === 0" @click="stageDeleteSelected">
              Stage delete selected
            </n-button>
          </n-space>
        </footer>
      </section>

      <aside class="document-inspector">
        <div class="document-panel-head">
          <div>
            <n-text class="document-panel-head__title">Inspector</n-text>
            <n-text depth="3" class="document-panel-head__meta">
              {{ selectedRow?.id ?? (activeCollectionName || 'No collection selected') }}
            </n-text>
          </div>
          <n-tag v-if="selectedRow" size="tiny" :bordered="false">v{{ selectedRow.version }}</n-tag>
        </div>

        <n-tabs v-model:value="inspectorTab" type="segment" size="small" class="document-tabs">
          <n-tab name="detail" tab="Detail" />
          <n-tab name="edit" tab="Edit" />
          <n-tab name="validator" tab="Validator" />
          <n-tab name="import" tab="Import" />
          <n-tab name="indexes" tab="Indexes" />
        </n-tabs>

        <section v-if="inspectorTab === 'detail'" class="document-inspector-section">
          <template v-if="selectedRow">
            <div class="document-detail-strip">
              <span>{{ selectedRow.id }}</span>
              <span>version {{ selectedRow.version }}</span>
              <span>{{ selectedRow.rawJson.length }} chars</span>
            </div>
            <div class="document-section-title">
              <span>JSON document</span>
              <n-button size="tiny" quaternary @click="copyText(selectedRow.rawJson, 'Document copied')">Copy</n-button>
            </div>
            <pre class="document-json-preview">{{ selectedRow.rawJson }}</pre>
          </template>
          <n-empty v-else description="Run a find query and select a document." />
        </section>

        <section v-else-if="inspectorTab === 'edit'" class="document-inspector-section">
          <n-input v-model:value="editId" size="small" placeholder="Document ID" />
          <n-input
            v-model:value="editJson"
            type="textarea"
            :autosize="{ minRows: 8, maxRows: 14 }"
            placeholder="{ }"
          />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" type="primary" :disabled="!activeCollectionName || !editId.trim()" @click="stageInsertDocument">
              Stage insert
            </n-button>
            <n-button size="small" secondary :disabled="!activeCollectionName || !editId.trim()" @click="stageReplaceDocument">
              Stage replace
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="!activeCollectionName || !editId.trim()" @click="stageDeleteEditorDocument">
              Stage delete
            </n-button>
          </n-space>
        </section>

        <section v-else-if="inspectorTab === 'validator'" class="document-inspector-section">
          <div class="document-query-row">
            <n-select v-model:value="validatorAction" size="small" :options="validatorActionOptions" />
            <n-button size="small" secondary :disabled="!activeCollectionName" @click="stageSaveValidator">
              Stage save
            </n-button>
            <n-button size="small" tertiary type="error" :disabled="!activeCollectionName || !activeCollection?.validator" @click="stageDropValidator">
              Stage drop
            </n-button>
          </div>
          <n-input
            v-model:value="validatorText"
            type="textarea"
            :autosize="{ minRows: 8, maxRows: 14 }"
            placeholder="{ &quot;rules&quot;: [{ &quot;path&quot;: &quot;$.site&quot;, &quot;required&quot;: true, &quot;type&quot;: &quot;string&quot; }], &quot;validationAction&quot;: &quot;error&quot; }"
          />
          <div class="document-section-title">
            <span>Sample precheck</span>
            <n-button size="tiny" secondary @click="precheckValidatorSample">Precheck</n-button>
          </div>
          <n-input
            v-model:value="validatorSampleText"
            type="textarea"
            :autosize="{ minRows: 4, maxRows: 8 }"
            placeholder="{ }"
          />
          <p class="document-validator-result">{{ validatorPrecheckResult || 'No precheck result.' }}</p>
        </section>

        <section v-else-if="inspectorTab === 'import'" class="document-inspector-section">
          <div class="document-query-row">
            <n-input v-model:value="importIdPath" size="small" placeholder="_id or $.id" />
            <n-select v-model:value="importMode" size="small" :options="importModeOptions" />
          </div>
          <n-input
            v-model:value="importText"
            type="textarea"
            :autosize="{ minRows: 9, maxRows: 16 }"
            placeholder="JSON array, JSONL, or { id, document } items"
          />
          <n-space size="small" align="center" :wrap="true">
            <n-button size="small" type="primary" :disabled="!activeCollectionName || !importText.trim()" @click="stageImportDocuments">
              Stage import
            </n-button>
            <n-button size="small" secondary :disabled="rows.length === 0" @click="exportLoadedJsonl">
              Export loaded JSONL
            </n-button>
          </n-space>
        </section>

        <section v-else class="document-inspector-section">
          <div class="document-section-title">
            <span>JSON path indexes</span>
          </div>
          <n-data-table
            :columns="jsonIndexColumns"
            :data="activeCollection?.jsonIndexes ?? []"
            :bordered="false"
            :pagination="false"
            :single-line="false"
            size="small"
          />

          <div class="document-section-title">
            <span>FullText indexes</span>
          </div>
          <n-data-table
            :columns="fullTextIndexColumns"
            :data="activeCollection?.fullTextIndexes ?? []"
            :bordered="false"
            :pagination="false"
            :single-line="false"
            size="small"
          />
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      class="document-result"
      title="Document operation result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeCollectionName || 'documents'}`"
      empty-description="Browse a collection or stage document operations to see results."
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
import { computed, h, ref, watch } from 'vue';
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
  aggregateDocuments,
  countDocuments,
  createDocumentCollection,
  deleteManyDocuments,
  deleteOneDocument,
  distinctDocuments,
  dropDocumentCollection,
  dropDocumentValidator,
  findDocuments,
  insertManyDocuments,
  insertOneDocument,
  setDocumentValidator,
  updateOneDocument,
  type DocumentAggregateStage,
  type DocumentDistinctResponse,
  type DocumentFilter,
  type DocumentFindRequest,
  type DocumentFindResponse,
  type DocumentItemResponse,
  type DocumentProjection,
  type DocumentSort,
  type DocumentValidator,
  type DocumentWriteResponse,
} from '@/api/documents';
import type { SqlResultSet } from '@/api/sql';
import {
  runMaintenance,
  type DocumentCollectionInfo,
  type DocumentFullTextIndexInfo,
  type DocumentJsonIndexInfo,
  type MaintenanceResponse,
} from '@/api/schema';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';
import { downloadText, safeFileStem } from '@/utils/resultExport';
import {
  createWriteApprovalPlan,
  type WriteApprovalItem,
  type WriteApprovalPlan,
  type WriteApprovalSeverity,
} from '@/utils/writeApproval';

const props = withDefaults(defineProps<{
  targetDb: string;
  collection: DocumentCollectionInfo | null;
  collections?: DocumentCollectionInfo[];
  loading?: boolean;
}>(), {
  collections: () => [],
  loading: false,
});

const emit = defineEmits<{
  selectCollection: [collection: DocumentCollectionInfo];
  refreshSchema: [];
}>();

type QueryTab = 'find' | 'aggregate' | 'distinct';
type InspectorTab = 'detail' | 'edit' | 'validator' | 'import' | 'indexes';
type ImportMode = 'insert' | 'replace';

interface DocumentRow {
  id: string;
  version: number;
  document: unknown;
  rawJson: string;
  preview: string;
}

interface PendingOperation {
  id: string;
  label: string;
  detail: string;
  severity: WriteApprovalSeverity;
  command: string;
  run: () => Promise<OperationOutcome>;
}

interface OperationOutcome {
  action: string;
  target: string;
  succeeded: boolean;
  affected: number;
  detail: string;
}

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const collectionFilter = ref('');
const newCollectionName = ref('');
const rows = ref<DocumentRow[]>([]);
const queryBusy = ref(false);
const countBusy = ref(false);
const confirmBusy = ref(false);
const errorMsg = ref('');
const gridFilter = ref('');
const checkedRowKeys = ref<DataTableRowKey[]>([]);
const selectedId = ref('');
const queryTab = ref<QueryTab>('find');
const inspectorTab = ref<InspectorTab>('detail');
const idsText = ref('');
const filterText = ref('');
const projectionText = ref('');
const sortText = ref('');
const aggregateText = ref('[\n  { "$limit": 20 }\n]');
const distinctPath = ref('$.site');
const distinctLimit = ref<number | null>(50);
const limit = ref<number | null>(100);
const skip = ref<number | null>(0);
const continuationToken = ref('');
const hasMore = ref(false);
const cursorExpiresAtUtc = ref<string | null>(null);
const totalCount = ref<number | null>(null);
const editId = ref('');
const editJson = ref('{\n  \n}');
const validatorAction = ref<'error' | 'warn'>('error');
const validatorText = ref(defaultValidatorText());
const validatorSampleText = ref('{\n  "site": "north"\n}');
const validatorPrecheckResult = ref('');
const importIdPath = ref('_id');
const importMode = ref<ImportMode>('insert');
const importText = ref('');
const pendingOperations = ref<PendingOperation[]>([]);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);

const collections = computed(() => props.collections);
const activeCollection = computed(() =>
  props.collection ?? collections.value[0] ?? null);
const activeCollectionName = computed(() => activeCollection.value?.name ?? '');

const selectedCollectionName = computed({
  get: () => activeCollectionName.value,
  set: (value: string) => selectCollection(value),
});

const collectionOptions = computed<SelectOption[]>(() =>
  collections.value.map((collection) => ({
    label: collection.name,
    value: collection.name,
  })));

const filteredCollections = computed(() => {
  const keyword = collectionFilter.value.trim().toLowerCase();
  const sorted = [...collections.value].sort((a, b) => a.name.localeCompare(b.name));
  if (!keyword) return sorted;
  return sorted.filter((collection) =>
    collection.name.toLowerCase().includes(keyword)
    || collection.jsonIndexes.some((index) => index.name.toLowerCase().includes(keyword) || index.path.toLowerCase().includes(keyword))
    || collection.fullTextIndexes.some((index) => index.name.toLowerCase().includes(keyword)));
});

const filteredRows = computed(() => {
  const keyword = gridFilter.value.trim().toLowerCase();
  if (!keyword) return rows.value;
  return rows.value.filter((row) =>
    row.id.toLowerCase().includes(keyword)
    || row.preview.toLowerCase().includes(keyword)
    || row.rawJson.toLowerCase().includes(keyword));
});

const selectedRow = computed(() =>
  rows.value.find((row) => row.id === selectedId.value) ?? rows.value[0] ?? null);

const statItems = computed(() => [
  { label: 'Collections', value: formatStat(collections.value.length) },
  { label: 'Documents', value: formatStat(totalCount.value) },
  { label: 'JSON indexes', value: formatStat(activeCollection.value?.jsonIndexes.length) },
  { label: 'FullText indexes', value: formatStat(activeCollection.value?.fullTextIndexes.length) },
  { label: 'Validator', value: activeCollection.value?.validator ? activeCollection.value.validator.validationAction ?? 'error' : '-' },
  { label: 'Loaded', value: formatStat(rows.value.length) },
]);

const querySummary = computed(() => {
  if (!activeCollectionName.value) return 'No document collection selected.';
  if (queryTab.value === 'aggregate') return `${activeCollectionName.value} · aggregate pipeline`;
  if (queryTab.value === 'distinct') return `${activeCollectionName.value} · distinct ${distinctPath.value || 'path'}`;
  return `${activeCollectionName.value} · limit ${limit.value ?? 100}${hasMore.value ? ' · more available' : ''}`;
});

const pagerText = computed(() => {
  const count = totalCount.value == null ? 'unknown count' : `${totalCount.value.toLocaleString()} total`;
  const page = hasMore.value ? `next cursor expires ${formatDate(cursorExpiresAtUtc.value)}` : 'end of current page';
  return `${rows.value.length} loaded · ${count} · ${page}`;
});

const resultSummary = computed(() => {
  if (!latestResult.value) return querySummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) {
    const affected = latestResult.value.end.recordsAffected >= 0
      ? `affected ${latestResult.value.end.recordsAffected}`
      : `${latestResult.value.end.rowCount} rows`;
    return `${affected} · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  }
  return 'Ready';
});

const previewPlan = computed<WriteApprovalPlan | null>(() => {
  if (pendingOperations.value.length === 0) return null;
  const items: WriteApprovalItem[] = pendingOperations.value.map((operation) => ({
    id: operation.id,
    command: operation.command,
    severity: operation.severity,
    label: operation.label,
    detail: operation.detail,
  }));
  return createWriteApprovalPlan({
    id: `document_${props.targetDb}_${activeCollectionName.value}_${pendingOperations.value.map((item) => item.id).join('_')}`,
    title: 'Document operation batch',
    target: `${props.targetDb}.${activeCollectionName.value || 'collections'}`,
    items,
  });
});

const validatorActionOptions: SelectOption[] = [
  { label: 'Reject invalid writes', value: 'error' },
  { label: 'Warn and allow invalid writes', value: 'warn' },
];

const importModeOptions: SelectOption[] = [
  { label: 'Insert only', value: 'insert' },
  { label: 'Replace existing', value: 'replace' },
];

const documentColumns = computed<DataTableColumns<DocumentRow>>(() => [
  { type: 'selection', width: 42 },
  {
    title: 'ID',
    key: 'id',
    width: 190,
    ellipsis: { tooltip: true },
    render: (row) => h('button', {
      type: 'button',
      class: ['document-id-button', row.id === selectedRow.value?.id ? 'is-active' : ''],
      onClick: () => { selectedId.value = row.id; },
    }, row.id),
  },
  {
    title: 'Version',
    key: 'version',
    width: 90,
    render: (row) => h('code', row.version.toString()),
  },
  {
    title: 'Preview',
    key: 'preview',
    minWidth: 360,
    ellipsis: { tooltip: true },
    render: (row) => h('span', { class: 'document-preview-cell' }, row.preview),
  },
]);

const jsonIndexColumns = computed<DataTableColumns<DocumentJsonIndexInfo>>(() => [
  {
    title: 'Name',
    key: 'name',
    minWidth: 130,
    ellipsis: { tooltip: true },
  },
  {
    title: 'Path',
    key: 'path',
    minWidth: 150,
    ellipsis: { tooltip: true },
    render: (row) => h('code', ((row.paths ?? [row.path]) as string[]).join(', ')),
  },
  {
    title: '',
    key: 'actions',
    width: 92,
    render: (row) => h(NButton, {
      size: 'tiny',
      secondary: true,
      disabled: !activeCollectionName.value || !row.rebuildable,
      onClick: () => stageRebuildIndex(row.name, 'document_json'),
    }, { default: () => 'Rebuild' }),
  },
]);

const fullTextIndexColumns = computed<DataTableColumns<DocumentFullTextIndexInfo>>(() => [
  {
    title: 'Name',
    key: 'name',
    minWidth: 130,
    ellipsis: { tooltip: true },
  },
  {
    title: 'Fields',
    key: 'fields',
    minWidth: 150,
    ellipsis: { tooltip: true },
    render: (row) => h('code', row.fields.join(', ')),
  },
  {
    title: '',
    key: 'actions',
    width: 92,
    render: (row) => h(NButton, {
      size: 'tiny',
      secondary: true,
      disabled: !activeCollectionName.value || !row.rebuildable,
      onClick: () => stageRebuildIndex(row.name, 'document_fulltext'),
    }, { default: () => 'Rebuild' }),
  },
]);

function selectCollection(name: string): void {
  const next = collections.value.find((collection) => collection.name === name);
  if (next) emit('selectCollection', next);
}

async function runFind(append: boolean): Promise<void> {
  if (!activeCollectionName.value) return;
  queryBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const requestResult = buildFindRequest(append);
  if (!requestResult.ok) {
    errorMsg.value = requestResult.message;
    queryBusy.value = false;
    return;
  }
  const command = `documents.find ${activeCollectionName.value}\n${JSON.stringify(requestResult.request, null, 2)}`;
  try {
    const response = await findDocuments(auth.api, props.targetDb, activeCollectionName.value, requestResult.request);
    const elapsed = performance.now() - started;
    applyFindResponse(response, append, elapsed, command);
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '文档查询失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('document_find_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'Document find', 'find', command, msg, 0, -1, elapsed);
  } finally {
    queryBusy.value = false;
  }
}

async function runCount(): Promise<void> {
  if (!activeCollectionName.value) return;
  countBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const ids = parseIds(idsText.value);
  const command = `documents.count ${activeCollectionName.value}${ids.length > 0 ? ` ${ids.length} ids` : ''}`;
  try {
    const response = await countDocuments(auth.api, props.targetDb, activeCollectionName.value, ids);
    const elapsed = performance.now() - started;
    totalCount.value = response.count;
    latestCommand.value = command;
    latestResult.value = {
      columns: ['collection', 'count'],
      rows: [[response.collection, response.count]],
      end: { type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: elapsed },
      error: null,
      hasColumns: true,
    };
    ranOnce.value = true;
    recordHistory('success', 'Document count', 'count', command, `${response.count} documents`, 1, -1, elapsed);
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '文档计数失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('document_count_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'Document count', 'count', command, msg, 0, -1, elapsed);
  } finally {
    countBusy.value = false;
  }
}

async function runDistinct(): Promise<void> {
  if (!activeCollectionName.value || !distinctPath.value.trim()) return;
  queryBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const request = {
    path: distinctPath.value.trim(),
    ids: parseIds(idsText.value),
    limit: distinctLimit.value ?? null,
  };
  const command = `documents.distinct ${activeCollectionName.value}\n${JSON.stringify(request, null, 2)}`;
  try {
    const response = await distinctDocuments(auth.api, props.targetDb, activeCollectionName.value, request);
    const elapsed = performance.now() - started;
    latestCommand.value = command;
    latestResult.value = resultFromDistinct(response, elapsed);
    ranOnce.value = true;
    recordHistory('success', 'Document distinct', 'distinct', command, `${response.values.length} values`, response.values.length, -1, elapsed);
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '文档 distinct 失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('document_distinct_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'Document distinct', 'distinct', command, msg, 0, -1, elapsed);
  } finally {
    queryBusy.value = false;
  }
}

async function runAggregate(): Promise<void> {
  if (!activeCollectionName.value) return;
  const parsed = parseJson<DocumentAggregateStage[]>(aggregateText.value, 'Aggregate pipeline must be a JSON array.');
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return;
  }
  queryBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const request = { pipeline: parsed.value };
  const command = `documents.aggregate ${activeCollectionName.value}\n${JSON.stringify(request, null, 2)}`;
  try {
    const response = await aggregateDocuments(auth.api, props.targetDb, activeCollectionName.value, request);
    const elapsed = performance.now() - started;
    latestCommand.value = command;
    latestResult.value = resultFromAggregate(response.documents, elapsed);
    ranOnce.value = true;
    recordHistory('success', 'Document aggregate', 'aggregate', command, `${response.count} documents`, response.count, -1, elapsed);
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '文档聚合失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('document_aggregate_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'Document aggregate', 'aggregate', command, msg, 0, -1, elapsed);
  } finally {
    queryBusy.value = false;
  }
}

function applyFindResponse(response: DocumentFindResponse, append: boolean, elapsed: number, command: string): void {
  const mapped = response.documents.map(mapDocument);
  rows.value = append ? mergeRows(rows.value, mapped) : mapped;
  checkedRowKeys.value = [];
  hasMore.value = response.hasMore;
  continuationToken.value = response.continuationToken ?? '';
  cursorExpiresAtUtc.value = response.cursorExpiresAtUtc ?? null;
  latestCommand.value = command;
  latestResult.value = resultFromDocuments(rows.value, elapsed);
  ranOnce.value = true;
  syncSelectedAfterRows();
  recordHistory('success', 'Document find', 'find', command, `${mapped.length} documents`, mapped.length, -1, elapsed);
}

function buildFindRequest(append: boolean): { ok: true; request: DocumentFindRequest } | { ok: false; message: string } {
  const ids = parseIds(idsText.value);
  const filter = parseOptionalJson<DocumentFilter>(filterText.value, 'Filter must be a JSON object.');
  if (!filter.ok) return filter;
  const projection = parseProjection(projectionText.value);
  if (!projection.ok) return projection;
  const sort = parseSort(sortText.value);
  if (!sort.ok) return sort;
  return {
    ok: true,
    request: {
      ids: ids.length > 0 ? ids : undefined,
      filter: filter.value,
      projection: projection.value,
      sort: sort.value,
      limit: limit.value ?? 100,
      skip: append ? 0 : skip.value ?? 0,
      continuationToken: append ? continuationToken.value || undefined : undefined,
    },
  };
}

function stageCreateCollection(): void {
  const name = newCollectionName.value.trim();
  if (!name) return;
  setPendingOperations([{
    id: makeOperationId('create_collection'),
    label: 'Create collection',
    detail: 'Creates a document collection through the existing Document API.',
    severity: 'write',
    command: `POST /v1/db/${props.targetDb}/documents/${name}`,
    run: async () => {
      const response = await createDocumentCollection(auth.api, props.targetDb, name, { ifNotExists: true });
      return outcomeFromCollection('create_collection', response.collection, response.status, response.status === 'created' ? 1 : 0);
    },
  }]);
}

function stageDropCollection(): void {
  const name = activeCollectionName.value;
  if (!name) return;
  setPendingOperations([{
    id: makeOperationId('drop_collection'),
    label: 'Drop collection',
    detail: 'Drops the collection metadata and data through the existing Document API.',
    severity: 'danger',
    command: `DELETE /v1/db/${props.targetDb}/documents/${name}`,
    run: async () => {
      const response = await dropDocumentCollection(auth.api, props.targetDb, name);
      return outcomeFromCollection('drop_collection', response.collection, response.status, response.status === 'dropped' ? 1 : 0);
    },
  }]);
}

function stageInsertDocument(): void {
  const parsed = parseEditorDocument();
  if (!parsed.ok) return;
  setPendingOperations([{
    id: makeOperationId('insert_doc'),
    label: 'Insert document',
    detail: `Inserts ${parsed.id} into ${activeCollectionName.value}.`,
    severity: 'write',
    command: `documents.insertOne ${activeCollectionName.value}/${parsed.id}\n${formatJson(parsed.document)}`,
    run: async () => outcomeFromWrite('insert_one', parsed.id, await insertOneDocument(auth.api, props.targetDb, activeCollectionName.value, {
      id: parsed.id,
      document: parsed.document,
    })),
  }]);
}

function stageReplaceDocument(): void {
  const parsed = parseEditorDocument();
  if (!parsed.ok) return;
  setPendingOperations([{
    id: makeOperationId('replace_doc'),
    label: 'Replace document',
    detail: `Replaces ${parsed.id} in ${activeCollectionName.value}.`,
    severity: 'write',
    command: `documents.updateOne ${activeCollectionName.value}/${parsed.id}\n${formatJson(parsed.document)}`,
    run: async () => outcomeFromWrite('replace_one', parsed.id, await updateOneDocument(auth.api, props.targetDb, activeCollectionName.value, {
      id: parsed.id,
      document: parsed.document,
    })),
  }]);
}

function stageDeleteEditorDocument(): void {
  const id = editId.value.trim();
  if (!activeCollectionName.value || !id) return;
  setPendingOperations([deleteOperation([id])]);
}

function stageDeleteSelected(): void {
  const ids = checkedRowKeys.value.map(String).filter(Boolean);
  if (ids.length === 0) return;
  setPendingOperations([deleteOperation(ids)]);
}

function deleteOperation(ids: string[]): PendingOperation {
  return {
    id: makeOperationId('delete_docs'),
    label: ids.length === 1 ? 'Delete document' : 'Delete documents',
    detail: ids.length === 1 ? `Deletes ${ids[0]}.` : `Deletes ${ids.length} selected documents.`,
    severity: 'danger',
    command: `documents.delete ${activeCollectionName.value}\n${ids.join('\n')}`,
    run: async () => {
      const response = ids.length === 1
        ? await deleteOneDocument(auth.api, props.targetDb, activeCollectionName.value, { id: ids[0] })
        : await deleteManyDocuments(auth.api, props.targetDb, activeCollectionName.value, { ids, ordered: true });
      return outcomeFromWrite('delete', activeCollectionName.value, response);
    },
  };
}

function stageSaveValidator(): void {
  if (!activeCollectionName.value) return;
  const parsed = parseJson<DocumentValidator>(validatorText.value, 'Validator must be a JSON object.');
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return;
  }
  const validator = normalizeValidatorDraft(parsed.value);
  setPendingOperations([{
    id: makeOperationId('set_validator'),
    label: 'Save validator',
    detail: `${validator.rules.length} rules · action ${validator.validationAction}`,
    severity: 'write',
    command: `PUT /v1/db/${props.targetDb}/documents/${activeCollectionName.value}/validator\n${JSON.stringify(validator, null, 2)}`,
    run: async () => {
      const response = await setDocumentValidator(auth.api, props.targetDb, activeCollectionName.value, validator);
      return {
        action: 'set_validator',
        target: response.collection,
        succeeded: response.status === 'updated',
        affected: response.validator?.rules.length ?? 0,
        detail: response.status,
      };
    },
  }]);
}

function stageDropValidator(): void {
  if (!activeCollectionName.value) return;
  setPendingOperations([{
    id: makeOperationId('drop_validator'),
    label: 'Drop validator',
    detail: 'Removes the collection validator.',
    severity: 'danger',
    command: `DELETE /v1/db/${props.targetDb}/documents/${activeCollectionName.value}/validator`,
    run: async () => {
      const response = await dropDocumentValidator(auth.api, props.targetDb, activeCollectionName.value);
      return {
        action: 'drop_validator',
        target: response.collection,
        succeeded: response.status === 'dropped',
        affected: response.status === 'dropped' ? 1 : 0,
        detail: response.status,
      };
    },
  }]);
}

function stageImportDocuments(): void {
  if (!activeCollectionName.value) return;
  const parsed = parseImportDocuments(importText.value, importIdPath.value.trim() || '_id');
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return;
  }
  const items = parsed.items;
  setPendingOperations([{
    id: makeOperationId('import_docs'),
    label: importMode.value === 'replace' ? 'Replace import' : 'Insert import',
    detail: `${items.length} documents parsed from JSONL / JSON input.`,
    severity: 'write',
    command: `documents.${importMode.value === 'replace' ? 'replaceMany' : 'insertMany'} ${activeCollectionName.value}\n${items.length} documents`,
    run: async () => {
      const response = importMode.value === 'replace'
        ? await updateOneByOne(items)
        : await insertManyDocuments(auth.api, props.targetDb, activeCollectionName.value, { documents: items, ordered: false });
      return outcomeFromWrite(importMode.value === 'replace' ? 'replace_import' : 'insert_import', activeCollectionName.value, response);
    },
  }]);
}

function stageRebuildIndex(indexName: string, targetModel: 'document_json' | 'document_fulltext'): void {
  if (!activeCollectionName.value) return;
  setPendingOperations([{
    id: makeOperationId('rebuild_index'),
    label: 'Rebuild index',
    detail: `${targetModel} ${activeCollectionName.value}.${indexName}`,
    severity: 'write',
    command: `rebuild_index ${targetModel} ${activeCollectionName.value}.${indexName}`,
    run: async () => {
      const response = await runMaintenance(auth.api, props.targetDb, {
        operation: 'rebuild_index',
        targetModel,
        targetOwner: activeCollectionName.value,
        targetName: indexName,
      });
      return outcomeFromMaintenance(response);
    },
  }]);
}

async function confirmPendingOperations(): Promise<void> {
  if (pendingOperations.value.length === 0) return;
  confirmBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const operations = [...pendingOperations.value];
  const command = operations.map((operation) => operation.command).join('\n');
  try {
    const outcomes: OperationOutcome[] = [];
    for (const operation of operations) {
      outcomes.push(await operation.run());
    }
    const elapsed = performance.now() - started;
    const affected = outcomes.reduce((sum, item) => sum + item.affected, 0);
    latestCommand.value = command;
    latestResult.value = resultFromOutcomes(outcomes, elapsed);
    ranOnce.value = true;
    pendingOperations.value = [];
    checkedRowKeys.value = [];
    recordHistory('success', 'Document operation batch', operations.map((operation) => operation.label).join(', '), command, `${outcomes.length} actions · affected ${affected}`, outcomes.length, affected, elapsed);
    message.success(`Committed ${outcomes.length} document action${outcomes.length === 1 ? '' : 's'}.`);
    emit('refreshSchema');
    await refreshAfterWrite();
  } catch (error) {
    const elapsed = performance.now() - started;
    const msg = errorToMessage(error, '提交文档操作失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('document_write_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'Document operation batch', 'confirm', command, msg, 0, 0, elapsed);
  } finally {
    confirmBusy.value = false;
  }
}

function clearPendingOperations(): void {
  pendingOperations.value = [];
}

function setPendingOperations(operations: PendingOperation[]): void {
  pendingOperations.value = operations;
  errorMsg.value = '';
}

async function refreshAfterWrite(): Promise<void> {
  if (!activeCollectionName.value) return;
  await runCount();
  await runFind(false);
}

async function updateOneByOne(items: Array<{ id: string; document: unknown }>): Promise<DocumentWriteResponse> {
  let inserted = 0;
  let matched = 0;
  let modified = 0;
  const errors: NonNullable<DocumentWriteResponse['errors']> = [];
  for (let index = 0; index < items.length; index += 1) {
    const item = items[index];
    try {
      const response = await updateOneDocument(auth.api, props.targetDb, activeCollectionName.value, {
        id: item.id,
        document: item.document,
      });
      inserted += response.inserted ?? 0;
      matched += response.matched ?? 0;
      modified += response.modified ?? 0;
      if (response.errors) errors.push(...response.errors);
    } catch (error) {
      errors.push({
        index,
        id: item.id,
        code: 'replace_failed',
        message: errorToMessage(error, 'replace failed'),
        severity: 'error',
      });
    }
  }
  return {
    collection: activeCollectionName.value,
    inserted,
    matched,
    modified,
    deleted: 0,
    errors: errors.length > 0 ? errors : null,
  };
}

function precheckValidatorSample(): void {
  const validatorParsed = parseJson<DocumentValidator>(validatorText.value, 'Validator must be a JSON object.');
  if (!validatorParsed.ok) {
    validatorPrecheckResult.value = validatorParsed.message;
    return;
  }
  const sampleParsed = parseJson<unknown>(validatorSampleText.value, 'Sample must be valid JSON.');
  if (!sampleParsed.ok) {
    validatorPrecheckResult.value = sampleParsed.message;
    return;
  }
  const failures = validateSample(normalizeValidatorDraft(validatorParsed.value), sampleParsed.value);
  validatorPrecheckResult.value = failures.length === 0
    ? 'Sample passes the visible validator draft.'
    : failures.join(' · ');
}

function exportLoadedJsonl(): void {
  if (rows.value.length === 0) return;
  const lines = rows.value.map((row) => JSON.stringify({ id: row.id, document: row.document }));
  const fileName = `${safeFileStem(`${props.targetDb}_${activeCollectionName.value || 'documents'}`, 'documents')}.jsonl`;
  downloadText(fileName, `${lines.join('\n')}\n`, 'application/x-ndjson;charset=utf-8');
  message.success(`Exported ${rows.value.length} loaded documents.`);
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function rowKey(row: DocumentRow): string {
  return row.id;
}

function mapDocument(item: DocumentItemResponse): DocumentRow {
  const rawJson = formatJson(item.document);
  return {
    id: item.id,
    version: item.version,
    document: item.document,
    rawJson,
    preview: rawJson.replace(/\s+/g, ' ').slice(0, 240),
  };
}

function mergeRows(existing: DocumentRow[], incoming: DocumentRow[]): DocumentRow[] {
  const map = new Map(existing.map((row) => [row.id, row]));
  for (const row of incoming) map.set(row.id, row);
  return [...map.values()].sort((a, b) => a.id.localeCompare(b.id));
}

function syncSelectedAfterRows(): void {
  if (selectedId.value && rows.value.some((row) => row.id === selectedId.value)) return;
  selectedId.value = rows.value[0]?.id ?? '';
}

function parseIds(text: string): string[] {
  return [...new Set(text.split(/\r?\n|,/g).map((item) => item.trim()).filter(Boolean))];
}

function parseProjection(text: string): { ok: true; value?: DocumentProjection[] } | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, value: undefined };
  if (trimmed.startsWith('[')) {
    const parsed = parseJson<DocumentProjection[]>(trimmed, 'Projection must be a JSON array.');
    return parsed.ok ? { ok: true, value: parsed.value } : parsed;
  }
  return {
    ok: true,
    value: trimmed.split(/\r?\n/g).map((line) => line.trim()).filter(Boolean).map((line) => {
      const index = line.indexOf('=');
      return index > 0
        ? { name: line.slice(0, index).trim(), path: line.slice(index + 1).trim() }
        : { path: line };
    }),
  };
}

function parseSort(text: string): { ok: true; value?: DocumentSort[] } | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, value: undefined };
  if (trimmed.startsWith('[')) {
    const parsed = parseJson<DocumentSort[]>(trimmed, 'Sort must be a JSON array.');
    return parsed.ok ? { ok: true, value: parsed.value } : parsed;
  }
  return {
    ok: true,
    value: trimmed.split(/\r?\n/g).map((line) => line.trim()).filter(Boolean).map((line) => {
      const [path, direction] = line.split(/\s+/g);
      return { path, descending: /^desc/i.test(direction ?? '') };
    }),
  };
}

function parseEditorDocument(): { ok: true; id: string; document: unknown } | { ok: false } {
  if (!activeCollectionName.value) return { ok: false };
  const id = editId.value.trim();
  if (!id) {
    errorMsg.value = 'Document ID is required.';
    return { ok: false };
  }
  const parsed = parseJson<unknown>(editJson.value, 'Document body must be valid JSON.');
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    return { ok: false };
  }
  return { ok: true, id, document: parsed.value };
}

function parseImportDocuments(text: string, idPath: string):
  | { ok: true; items: Array<{ id: string; document: unknown }> }
  | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: false, message: 'Import input is empty.' };
  let values: unknown[];
  if (trimmed.startsWith('[')) {
    const parsed = parseJson<unknown[]>(trimmed, 'Import JSON array is invalid.');
    if (!parsed.ok) return parsed;
    values = parsed.value;
  } else {
    const lines = trimmed.split(/\r?\n/g).map((line) => line.trim()).filter(Boolean);
    values = [];
    for (const line of lines) {
      const parsed = parseJson<unknown>(line, `Invalid JSONL line: ${line.slice(0, 80)}`);
      if (!parsed.ok) return parsed;
      values.push(parsed.value);
    }
  }

  const items = values.map((value, index) => {
    const maybePair = value && typeof value === 'object' ? value as Record<string, unknown> : null;
    if (maybePair && typeof maybePair.id === 'string' && 'document' in maybePair) {
      return { id: maybePair.id, document: maybePair.document };
    }
    const idValue = readPath(value, idPath);
    const id = idValue == null || idValue === ''
      ? `doc-${Date.now().toString(36)}-${index + 1}`
      : String(idValue);
    return { id, document: value };
  });

  return items.length === 0
    ? { ok: false, message: 'No documents parsed from import input.' }
    : { ok: true, items };
}

function parseJson<T>(text: string, messageText: string): { ok: true; value: T } | { ok: false; message: string } {
  try {
    return { ok: true, value: JSON.parse(text) as T };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : messageText };
  }
}

function parseOptionalJson<T>(text: string, messageText: string): { ok: true; value?: T } | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, value: undefined };
  return parseJson<T>(trimmed, messageText);
}

function normalizeValidatorDraft(validator: DocumentValidator): DocumentValidator {
  return {
    rules: Array.isArray(validator.rules) ? validator.rules : [],
    validationAction: validatorAction.value || validator.validationAction || 'error',
  };
}

function validateSample(validator: DocumentValidator, sample: unknown): string[] {
  const failures: string[] = [];
  for (const rule of validator.rules) {
    const value = readPath(sample, rule.path);
    if (rule.required && (value === undefined || value === null)) {
      failures.push(`${rule.path} is required`);
      continue;
    }
    if (value === undefined || value === null) continue;
    const types = rule.types && rule.types.length > 0 ? rule.types : rule.type ? [rule.type] : [];
    if (types.length > 0 && !types.some((type) => matchesJsonType(value, type))) {
      failures.push(`${rule.path} type mismatch`);
    }
    if (typeof value === 'number') {
      if (typeof rule.minimum === 'number' && value < rule.minimum) failures.push(`${rule.path} < ${rule.minimum}`);
      if (typeof rule.maximum === 'number' && value > rule.maximum) failures.push(`${rule.path} > ${rule.maximum}`);
    }
    if (typeof value === 'string' && rule.pattern) {
      try {
        if (!new RegExp(rule.pattern).test(value)) failures.push(`${rule.path} pattern mismatch`);
      } catch {
        failures.push(`${rule.path} pattern invalid`);
      }
    }
    if (rule.enum && rule.enum.length > 0) {
      const actual = JSON.stringify(value);
      if (!rule.enum.some((item) => JSON.stringify(item) === actual)) failures.push(`${rule.path} enum mismatch`);
    }
  }
  return failures;
}

function readPath(source: unknown, path: string): unknown {
  if (path === '_id' || path === 'id') {
    return source && typeof source === 'object' ? (source as Record<string, unknown>)._id ?? (source as Record<string, unknown>).id : undefined;
  }
  if (!path.startsWith('$.')) return undefined;
  const parts = path.slice(2).split('.').filter(Boolean);
  let current = source;
  for (const part of parts) {
    const key = part.replace(/^\['?/, '').replace(/'?\]$/, '');
    if (current && typeof current === 'object' && key in current) {
      current = (current as Record<string, unknown>)[key];
    } else {
      return undefined;
    }
  }
  return current;
}

function matchesJsonType(value: unknown, type: string): boolean {
  const normalized = type.toLowerCase();
  if (normalized === 'string') return typeof value === 'string';
  if (normalized === 'number') return typeof value === 'number';
  if (normalized === 'integer' || normalized === 'int') return typeof value === 'number' && Number.isInteger(value);
  if (normalized === 'boolean' || normalized === 'bool') return typeof value === 'boolean';
  if (normalized === 'object') return Boolean(value && typeof value === 'object' && !Array.isArray(value));
  if (normalized === 'array') return Array.isArray(value);
  if (normalized === 'null') return value === null;
  return false;
}

function resultFromDocuments(items: DocumentRow[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['id', 'version', 'preview', 'json'],
    rows: items.map((item) => [item.id, item.version, item.preview, item.rawJson]),
    end: { type: 'end', rowCount: items.length, recordsAffected: -1, elapsedMs },
    error: null,
    hasColumns: true,
  };
}

function resultFromDistinct(response: DocumentDistinctResponse, elapsedMs: number): SqlResultSet {
  return {
    columns: ['path', 'value'],
    rows: response.values.map((value) => [response.path, formatJson(value)]),
    end: { type: 'end', rowCount: response.values.length, recordsAffected: -1, elapsedMs },
    error: null,
    hasColumns: true,
  };
}

function resultFromAggregate(documents: unknown[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['row', 'json'],
    rows: documents.map((document, index) => [index + 1, formatJson(document)]),
    end: { type: 'end', rowCount: documents.length, recordsAffected: -1, elapsedMs },
    error: null,
    hasColumns: true,
  };
}

function resultFromOutcomes(outcomes: OperationOutcome[], elapsedMs: number): SqlResultSet {
  return {
    columns: ['action', 'target', 'succeeded', 'affected', 'detail'],
    rows: outcomes.map((item) => [item.action, item.target, item.succeeded, item.affected, item.detail]),
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

function errorResult(code: string, messageText: string): SqlResultSet {
  return {
    columns: [],
    rows: [],
    end: null,
    error: { type: 'error', code, message: messageText },
    hasColumns: false,
  };
}

function outcomeFromCollection(action: string, target: string, status: string, affected: number): OperationOutcome {
  return { action, target, succeeded: !['missing', 'failed'].includes(status), affected, detail: status };
}

function outcomeFromWrite(action: string, target: string, response: DocumentWriteResponse): OperationOutcome {
  const affected = (response.inserted ?? 0) + (response.modified ?? 0) + (response.deleted ?? 0);
  const errors = response.errors?.filter((item) => item.severity !== 'warning') ?? [];
  return {
    action,
    target,
    succeeded: errors.length === 0,
    affected,
    detail: writeSummary(response),
  };
}

function outcomeFromMaintenance(response: MaintenanceResponse): OperationOutcome {
  return {
    action: response.operation,
    target: `${response.index?.owner ?? activeCollectionName.value}.${response.index?.name ?? ''}`,
    succeeded: response.success,
    affected: response.index?.documentCount ?? 0,
    detail: response.message,
  };
}

function writeSummary(response: DocumentWriteResponse): string {
  const parts = [
    response.inserted ? `inserted ${response.inserted}` : '',
    response.matched ? `matched ${response.matched}` : '',
    response.modified ? `modified ${response.modified}` : '',
    response.deleted ? `deleted ${response.deleted}` : '',
    response.errors?.length ? `${response.errors.length} warnings/errors` : '',
  ].filter(Boolean);
  return parts.join(' · ') || 'no changes';
}

function formatJson(value: unknown): string {
  try {
    const text = JSON.stringify(value, null, 2);
    return typeof text === 'string' ? text : String(value ?? '');
  } catch {
    return String(value ?? '');
  }
}

function collectionMeta(collection: DocumentCollectionInfo): string {
  return `${collection.jsonIndexes.length} json · ${collection.fullTextIndexes.length} fulltext · ${collection.validator ? 'validator' : 'no validator'}`;
}

function defaultValidatorText(): string {
  return JSON.stringify({
    rules: [
      { path: '$.site', required: true, type: 'string' },
    ],
    validationAction: 'error',
  }, null, 2);
}

function formatStat(value?: number | null): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString() : '-';
}

function formatDate(value?: string | null): string {
  if (!value) return '-';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString();
}

function makeOperationId(prefix: string): string {
  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function errorToMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const response = (error as { response?: { data?: unknown } }).response;
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

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning(text);
  }
}

function recordHistory(
  status: 'success' | 'error',
  title: string,
  action: string,
  command: string,
  summary: string,
  rowCount: number,
  recordsAffected: number,
  elapsedMs: number,
): void {
  history.record({
    kind: action === 'find' || action === 'aggregate' || action === 'distinct' || action === 'count' ? 'query' : 'operation',
    status,
    title,
    target: activeCollectionName.value,
    database: props.targetDb,
    connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name,
    model: 'document',
    action,
    command,
    summary,
    rowCount,
    recordsAffected,
    elapsedMs,
  });
}

watch(selectedRow, (row) => {
  editId.value = row?.id ?? editId.value;
  editJson.value = row ? row.rawJson : editJson.value;
});

watch(activeCollection, (collection) => {
  rows.value = [];
  selectedId.value = '';
  checkedRowKeys.value = [];
  hasMore.value = false;
  continuationToken.value = '';
  totalCount.value = null;
  if (collection?.validator) {
    validatorAction.value = collection.validator.validationAction === 'warn' ? 'warn' : 'error';
    validatorText.value = JSON.stringify(collection.validator, null, 2);
  } else {
    validatorAction.value = 'error';
    validatorText.value = defaultValidatorText();
  }
  if (collection && props.targetDb) {
    void runCount();
    void runFind(false);
  }
}, { immediate: true });

watch(validatorAction, (action) => {
  const parsed = parseJson<DocumentValidator>(validatorText.value, 'Validator must be a JSON object.');
  if (!parsed.ok) return;
  validatorText.value = JSON.stringify({ ...parsed.value, validationAction: action }, null, 2);
});

</script>

<style scoped>
.document-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.document-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #f8fbff;
}

.document-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.document-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.document-toolbar__meta,
.document-panel-head__meta {
  font-size: 12px;
}

.document-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.document-toolbar__collection {
  width: 210px;
}

.document-toolbar__limit {
  width: 86px;
}

.document-alert {
  margin: 10px 12px 0;
}

.document-stats {
  display: grid;
  grid-template-columns: repeat(6, minmax(110px, 1fr));
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.document-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.document-stat span {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.document-stat strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 15px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-body {
  display: grid;
  flex: 1;
  min-height: 440px;
  grid-template-columns: 240px minmax(430px, 1fr) 390px;
  min-width: 0;
  overflow: hidden;
}

.document-collections,
.document-query-panel,
.document-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.document-collections {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.document-inspector {
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffdf8;
}

.document-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.document-panel-head--grid {
  align-items: center;
}

.document-panel-head__title,
.document-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.document-create {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 9px 10px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.document-collection-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 8px;
}

.document-collection-card,
.document-id-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.document-collection-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-left: 2px solid rgba(32, 128, 240, 0.45);
  border-radius: 6px;
  text-align: left;
}

.document-collection-card:hover,
.document-collection-card.is-active,
.document-id-button:hover,
.document-id-button.is-active {
  background: rgba(32, 128, 240, 0.08);
}

.document-collection-card.is-active {
  border-left-color: rgba(32, 128, 240, 0.9);
}

.document-collection-card span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-collection-card small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.document-query-editor {
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.document-editor-grid,
.document-query-row {
  display: grid;
  gap: 8px;
  align-items: center;
}

.document-editor-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.document-query-row {
  grid-template-columns: minmax(0, 110px) minmax(0, 1fr) auto;
}

.document-query-row--distinct {
  grid-template-columns: minmax(0, 1fr) 110px auto;
}

.document-grid {
  flex: 1;
  min-height: 0;
}

.document-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.document-id-button {
  max-width: 100%;
  overflow: hidden;
  padding: 2px 5px;
  border-radius: 4px;
  color: var(--sndb-ink-strong);
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 700;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-preview-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: #345;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.document-pager {
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

.document-grid-filter {
  width: 190px;
}

.document-tabs {
  flex: 0 0 auto;
  padding: 8px 12px 0;
}

.document-inspector-section {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 0;
  overflow: auto;
  padding: 10px 12px 12px;
}

.document-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.document-detail-strip span {
  padding: 2px 7px;
  border-radius: 999px;
  background: rgba(32, 128, 240, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.document-section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.document-json-preview {
  flex: 1;
  min-height: 180px;
  margin: 0;
  overflow: auto;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.document-validator-result {
  min-height: 38px;
  margin: 0;
  padding: 8px;
  border-radius: 6px;
  background: rgba(32, 128, 240, 0.06);
  color: var(--sndb-ink-soft);
  font-size: 12px;
  line-height: 1.45;
}

.document-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1420px) {
  .document-body {
    grid-template-columns: 230px minmax(420px, 1fr);
  }

  .document-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .document-toolbar,
  .document-panel-head--grid,
  .document-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .document-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .document-collections,
  .document-inspector {
    border-right: 0;
    border-left: 0;
  }

  .document-stats,
  .document-editor-grid,
  .document-query-row,
  .document-query-row--distinct {
    grid-template-columns: 1fr;
  }

  .document-toolbar__collection,
  .document-toolbar__limit,
  .document-grid-filter {
    width: 100%;
  }
}
</style>
