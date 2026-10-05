<template>
  <main
    class="fulltext-workbench"
    data-testid="workbench-fulltext"
    :data-page-state="fulltextState"
    :data-database="contextSnapshot.database"
    :data-resource-key="activeIndexKey"
    :data-legacy-key="activeIndexKey"
  >
    <section class="fulltext-toolbar">
      <div class="fulltext-toolbar__identity">
        <n-space size="small" align="center" :wrap="true">
          <n-tag size="small" type="default" :bordered="false">FullText</n-tag>
          <n-text class="fulltext-toolbar__title">{{ activeIndexLabel || 'No fulltext index selected' }}</n-text>
          <n-tag v-if="activeIndex && !permissionDenied" size="tiny" :bordered="false">{{ activeIndex.tokenizer }}</n-tag>
        </n-space>
        <n-text depth="3" class="fulltext-toolbar__meta">
          {{ targetDb || 'database' }}<template v-if="!permissionDenied"> · {{ formatStat(activeIndex?.documentCount) }} docs · {{ formatStat(activeIndex?.termCount) }} terms</template>
        </n-text>
        <n-text depth="3" class="fulltext-toolbar__context" data-testid="fulltext-context">
          {{ contextSnapshot.database || 'database' }} · {{ contextSnapshot.collection || 'collection' }} · {{ contextSnapshot.index || 'index' }}
        </n-text>
      </div>

      <div class="fulltext-toolbar__actions">
        <n-select
          v-model:value="selectedIndexKey"
          size="small"
          :options="indexOptions"
          :disabled="indexOptions.length === 0"
          class="fulltext-toolbar__index"
        />
        <n-select v-model:value="field" size="small" :options="fieldOptions" class="fulltext-toolbar__field" />
        <n-input-number
          v-model:value="topK"
          size="small"
          :min="1"
          :max="100"
          :show-button="false"
          placeholder="Top-K"
          class="fulltext-toolbar__topk"
        />
        <n-button size="small" secondary :loading="loadingIndexes || searching" @click="$emit('refreshSchema')">
          Refresh
        </n-button>
        <n-button size="small" secondary :disabled="!activeIndex || permissionDenied || readOnly || confirmBusy" @click="stageRebuild">
          Rebuild
        </n-button>
        <n-button size="small" quaternary @click="historyVisible = true">History</n-button>
      </div>
    </section>

    <WorkbenchSectionTabs
      :model-value="activeView"
      :items="fulltextSections"
      aria-label="全文工作区"
      @update:model-value="activeView = $event as FullTextView"
    />

    <WriteApprovalPanel
      v-if="previewPlan"
      :plan="previewPlan"
      :busy="confirmBusy"
      @cancel="clearPreviewPlan"
      @confirm="confirmPendingWrite"
    />

    <n-alert
      v-if="errorMsg"
      type="error"
      :title="errorMsg"
      closable
      class="fulltext-alert"
      @close="errorMsg = ''"
    />

    <n-alert
      v-if="permissionDenied"
      type="warning"
      title="FullText read permission required"
      data-testid="fulltext-permission-lock"
      class="fulltext-alert"
    >
      当前身份没有全文检索读取权限；查询草稿已保留，命中与文档载荷已清除。
    </n-alert>

    <section v-if="!permissionDenied" class="fulltext-stats">
      <article v-for="item in statItems" :key="item.label" class="fulltext-stat">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </section>

    <section class="fulltext-body" :class="`is-${activeView}`">
      <aside v-if="activeView !== 'analyzer' && !permissionDenied" class="fulltext-indexes">
        <div class="fulltext-panel-head">
          <div>
            <n-text class="fulltext-panel-head__title">FullText indexes</n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">{{ filteredIndexes.length }} visible · {{ indexes.length }} total</n-text>
          </div>
        </div>
        <n-input
          v-model:value="indexFilter"
          size="small"
          clearable
          placeholder="Filter indexes"
          class="fulltext-index-filter"
        />
        <div class="fulltext-index-list">
          <button
            v-for="item in filteredIndexes"
            :key="indexKey(item)"
            type="button"
            class="fulltext-index-card"
            :class="{ 'is-active': indexKey(item) === activeIndexKey }"
            @click="selectIndex(item)"
          >
            <span>{{ item.collection }}.{{ item.name }}</span>
            <small>{{ item.tokenizer }} · {{ item.fields.join(', ') || 'fields n/a' }}</small>
          </button>
          <n-empty v-if="filteredIndexes.length === 0" description="No fulltext indexes found." />
        </div>
      </aside>

      <section v-if="activeView === 'search'" class="fulltext-search-panel">
        <div class="fulltext-panel-head fulltext-panel-head--grid">
          <div>
            <n-text class="fulltext-panel-head__title">BM25 search playground</n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">{{ querySummary }}</n-text>
          </div>
          <n-space size="small" align="center" :wrap="true">
            <n-select v-model:value="mode" size="small" :options="modeOptions" class="fulltext-mode-select" />
            <n-select v-model:value="queryKind" size="small" :options="queryKindOptions" class="fulltext-kind-select" />
            <n-button size="small" secondary :disabled="!canSearch" :loading="searching" @click="runSearch">
              Search
            </n-button>
          </n-space>
        </div>

        <section class="fulltext-query-editor">
          <n-input
            v-model:value="queryText"
            type="textarea"
            :autosize="{ minRows: 4, maxRows: 8 }"
            placeholder="pump alarm"
            @keydown.ctrl.enter.prevent="runSearch"
          />

          <div class="fulltext-builder">
            <n-input
              v-model:value="builderText"
              size="small"
              clearable
              placeholder="Builder terms or phrase"
              @keydown.enter="applyBuilder('all')"
            />
            <n-space size="small" align="center" :wrap="true">
              <n-button size="small" quaternary @click="applyBuilder('all')">All</n-button>
              <n-button size="small" quaternary @click="applyBuilder('any')">Any</n-button>
              <n-button size="small" quaternary @click="applyBuilder('phrase')">Phrase</n-button>
              <n-button size="small" quaternary @click="applyFuzzyBuilder">Fuzzy</n-button>
            </n-space>
          </div>

          <div class="fulltext-param-strip">
            <span>
              <small>Collection</small>
              <strong>{{ activeIndex?.collection ?? '-' }}</strong>
            </span>
            <span>
              <small>Field</small>
              <strong>{{ effectiveField }}</strong>
            </span>
            <span>
              <small>Mode</small>
              <strong>{{ mode }}</strong>
            </span>
            <span>
              <small>Kind</small>
              <strong>{{ queryKind }}</strong>
            </span>
          </div>
        </section>

        <n-data-table
          :columns="hitColumns"
          :data="pagedRows"
          :loading="searching || loadingIndexes"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          :row-key="rowKey"
          size="small"
          remote
          flex-height
          class="fulltext-grid"
        />

        <footer class="fulltext-pager">
          <span :data-truncated="hitsTruncated ? 'true' : 'false'">
            {{ pageSummary }}<template v-if="hitsTruncated"> · truncated to {{ hits.length }} rows</template>
          </span>
          <n-space size="small" align="center">
            <n-select v-model:value="pageSize" size="small" :options="pageSizeOptions" class="fulltext-page-size" />
            <n-button size="small" secondary :disabled="page <= 1" @click="page -= 1">Previous</n-button>
            <n-button size="small" secondary :disabled="page >= pageCount" @click="page += 1">Next</n-button>
          </n-space>
        </footer>
      </section>

      <section v-if="activeView === 'import' && !permissionDenied && !readOnly" class="fulltext-import-panel">
        <div class="fulltext-panel-head">
          <div>
            <n-text class="fulltext-panel-head__title">独立全文数据导入</n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">
              写入 {{ activeIndex?.collection ?? 'collection' }}，由 {{ activeIndex?.name ?? 'fulltext index' }} 自动更新派生索引
            </n-text>
          </div>
          <n-button size="small" secondary :disabled="!activeIndex || permissionDenied || readOnly || confirmBusy" @click="fullTextFileInput?.click()">选择文件</n-button>
          <input ref="fullTextFileInput" type="file" accept=".json,.jsonl,.ndjson,application/json,application/x-ndjson" class="fulltext-file-input" @change="onFullTextFileSelected">
        </div>
        <div class="fulltext-import-options">
          <n-select v-model:value="importMode" size="small" :options="importModeOptions" />
          <n-input v-model:value="importIdPath" size="small" placeholder="ID path，例如 _id 或 id" />
          <n-tag size="small" :bordered="false">JSON / JSONL / NDJSON</n-tag>
        </div>
        <n-input
          v-model:value="importText"
          type="textarea"
          :autosize="{ minRows: 10, maxRows: 22 }"
          placeholder='{"_id":"doc-1","title":"Pump alarm","body":"..."}'
        />
        <div class="fulltext-import-actions">
          <span>{{ importSummary }}</span>
          <n-space size="small">
            <n-button size="small" quaternary :disabled="!importText" @click="clearFullTextImport">清空</n-button>
            <n-button size="small" type="primary" :disabled="!activeIndex || !importText.trim() || permissionDenied || readOnly || confirmBusy" @click="stageFullTextImport">解析并暂存</n-button>
          </n-space>
        </div>
      </section>

      <n-empty v-if="activeView === 'import' && readOnly && !permissionDenied" description="当前宿主为只读，全文导入不可用。" />

      <aside v-if="!permissionDenied" class="fulltext-inspector">
        <div class="fulltext-panel-head">
          <div>
            <n-text class="fulltext-panel-head__title">
              {{ activeView === 'search' ? '命中详情' : activeView === 'analyzer' ? 'Analyzer 预览' : '索引详情' }}
            </n-text>
            <n-text depth="3" class="fulltext-panel-head__meta">
              {{ activeView === 'search' ? (selectedHit ? selectedHit.documentId : '尚未选择命中项') : activeIndexLabel }}
            </n-text>
          </div>
          <n-tag v-if="selectedHit" size="tiny" :bordered="false">score {{ formatScore(selectedHit.score) }}</n-tag>
        </div>

        <template v-if="activeView === 'search' && selectedHit">
          <div class="fulltext-detail-strip">
            <span>rank {{ selectedHit.rank }}</span>
            <span>version {{ selectedHit.version ?? '-' }}</span>
            <span>{{ selectedHit.fieldName }}</span>
          </div>

          <section class="fulltext-section">
            <div class="fulltext-section-title">
              <span>Highlight</span>
              <n-button size="tiny" quaternary @click="copyText(selectedHit.snippetText, 'Snippet copied')">Copy</n-button>
            </div>
            <p class="fulltext-snippet">
              <template v-for="(part, index) in selectedHit.snippetParts" :key="`${selectedHit.key}:${index}`">
                <mark v-if="part.hit">{{ part.text }}</mark>
                <span v-else>{{ part.text }}</span>
              </template>
            </p>
          </section>

          <section class="fulltext-section fulltext-document">
            <div class="fulltext-section-title">
              <span>Document</span>
              <n-button size="tiny" quaternary @click="copyText(selectedHit.rawJson, 'Document copied')">Copy</n-button>
            </div>
            <pre>{{ selectedHit.rawJson }}</pre>
          </section>
        </template>
        <n-empty v-else-if="activeView === 'search'" description="执行检索并选择一条命中记录。" />

        <section v-if="activeView === 'analyzer'" class="fulltext-analyzer">
          <n-text class="fulltext-section-title fulltext-section-title--standalone">Analyzer preview</n-text>
          <div class="fulltext-analyzer-row">
            <n-select v-model:value="analyzeTokenizer" size="small" :options="tokenizerOptions" />
            <n-button size="small" secondary :disabled="permissionDenied" :loading="analyzing" @click="runAnalyze(false)">Analyze</n-button>
            <n-button size="small" quaternary :disabled="!queryText.trim() || permissionDenied" @click="runAnalyze(true)">
              Query
            </n-button>
          </div>
          <n-input
            v-model:value="analyzeText"
            type="textarea"
            :autosize="{ minRows: 3, maxRows: 6 }"
            placeholder="Text to analyze"
          />
          <div class="fulltext-token-list">
            <span v-for="token in analyzeTokens" :key="`${token.text}:${token.startOffset}:${token.endOffset}`">
              <strong>{{ token.text }}</strong>
              <small>{{ token.startOffset }}-{{ token.endOffset }} · +{{ token.positionIncrement }}</small>
            </span>
            <n-empty v-if="analyzeTokens.length === 0" description="No tokens yet." />
          </div>
        </section>

        <section v-if="activeView === 'index'" class="fulltext-index-detail">
          <dl>
            <div><dt>Collection</dt><dd>{{ activeIndex?.collection ?? '-' }}</dd></div>
            <div><dt>Index</dt><dd>{{ activeIndex?.name ?? '-' }}</dd></div>
            <div><dt>Tokenizer</dt><dd>{{ activeIndex?.tokenizer ?? '-' }}</dd></div>
            <div><dt>Documents</dt><dd>{{ formatStat(activeIndex?.documentCount) }}</dd></div>
            <div><dt>Terms</dt><dd>{{ formatStat(activeIndex?.termCount) }}</dd></div>
            <div><dt>Fields</dt><dd>{{ activeIndex?.fields.join(', ') || '-' }}</dd></div>
          </dl>
          <n-button type="primary" :disabled="!activeIndex || permissionDenied || readOnly || confirmBusy" @click="stageRebuild">暂存重建索引</n-button>
        </section>
      </aside>
    </section>

    <WorkbenchResultPanel
      v-if="!permissionDenied"
      class="fulltext-result"
      title="FullText search result"
      :sql="latestCommand"
      :result="latestResult"
      :ran-once="ranOnce"
      :summary="resultSummary"
      :file-name="`${targetDb}_${activeIndex?.collection ?? 'fulltext'}_${activeIndex?.name ?? 'search'}`"
      empty-description="No fulltext search result yet."
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
import { computed, h, onBeforeUnmount, ref, watch } from 'vue';
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
import type { FullTextIndexStat } from '@/api/management';
import { createApiClient } from '@/api/client';
import type { DocumentFindResponse, DocumentItemResponse, DocumentWriteResponse } from '@/api/documents';
import { insertManyDocuments, updateOneDocument } from '@/api/documents';
import {
  type FullTextAnalyzeResponse,
  type FullTextQueryKind,
  type FullTextSearchMode,
  type FullTextSearchPreviewHit,
  type FullTextSearchPreviewRequest,
  type FullTextSearchPreviewResponse,
  type FullTextTokenInfo,
} from '@/api/fulltext';
import type { SqlResultSet } from '@/api/sql';
import { quote } from '@/api/sql';
import {
  runMaintenance,
  type MaintenanceResponse,
} from '@/api/schema';
import WorkbenchHistoryDrawer from '@/components/WorkbenchHistoryDrawer.vue';
import WorkbenchResultPanel from '@/components/WorkbenchResultPanel.vue';
import WorkbenchSectionTabs, { type WorkbenchSectionTab } from '@/components/WorkbenchSectionTabs.vue';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import {
  useWorkbenchHistoryStore,
  type WorkbenchHistoryCompleteness,
  type WorkbenchHistoryEntry,
} from '@/stores/workbenchHistory';
import { createWriteApprovalPlan, type WriteApprovalPlan } from '@/utils/writeApproval';
import { formatSqlIdentifier } from '@/utils/sqlWorkbench';

const props = withDefaults(defineProps<{
  targetDb: string;
  index: FullTextIndexStat | null;
  indexes?: FullTextIndexStat[];
  loading?: boolean;
  /** 只读宿主保留检索、分词预览和导出，禁用全文重建和导入。 */
  readOnly?: boolean;
  /** 外部权限拒绝隐藏全文命中、文档与索引载荷。 */
  permissionDenied?: boolean;
}>(), {
  indexes: () => [],
  loading: false,
  readOnly: false,
  permissionDenied: false,
});

const emit = defineEmits<{
  selectIndex: [index: FullTextIndexStat];
  refreshSchema: [];
}>();

interface HighlightPart {
  text: string;
  hit: boolean;
}

interface HitRow {
  key: string;
  rank: number;
  documentId: string;
  score: number;
  fieldName: string;
  fieldText: string;
  snippetText: string;
  snippetParts: HighlightPart[];
  rawJson: string;
  version?: number;
  raw: FullTextSearchPreviewHit;
}

interface FullTextContextSnapshot {
  connectionId: string;
  connectionName: string;
  endpoint: string;
  profileEndpoint: string;
  database: string;
  collection: string;
  index: string;
  authToken: string;
  schemaSignature: string;
  api: AxiosInstance;
  requestApi?: AxiosInstance;
}

const FullTextLocalHitBudget = 100;
const FullTextImportMaxDocuments = 1000;

const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();
type FullTextView = 'search' | 'analyzer' | 'import' | 'index';
const activeView = ref<FullTextView>('search');
const fulltextSections: WorkbenchSectionTab[] = [
  { key: 'search', label: '全文检索' },
  { key: 'analyzer', label: 'Analyzer' },
  { key: 'import', label: '数据导入' },
  { key: 'index', label: '索引' },
];

const queryText = ref('pump alarm');
const builderText = ref('pump alarm');
const mode = ref<FullTextSearchMode>('exact');
const queryKind = ref<FullTextQueryKind>('all');
const field = ref('*');
const topK = ref<number | null>(20);
const page = ref(1);
const pageSize = ref(10);
const indexFilter = ref('');
const searching = ref(false);
const analyzing = ref(false);
const confirmBusy = ref(false);
const errorMsg = ref('');
const permissionLocked = ref(false);
const permissionDenied = computed(() => props.permissionDenied || permissionLocked.value);
const readOnly = computed(() => props.readOnly);
const hits = ref<FullTextSearchPreviewHit[]>([]);
const hitsTruncated = ref(false);
const resultField = ref('*');
const resultQuery = ref('');
const documentsById = ref<Record<string, DocumentItemResponse>>({});
const selectedId = ref('');
const analyzeText = ref('Pump alarm in north station');
const analyzeTokenizer = ref('unicode');
const analyzeTokens = ref<FullTextTokenInfo[]>([]);
const searchTokens = ref<string[]>([]);
const latestResult = ref<SqlResultSet | null>(null);
const latestCommand = ref('');
const ranOnce = ref(false);
const historyVisible = ref(false);
const previewPlan = ref<WriteApprovalPlan | null>(null);
const pendingRebuild = ref<FullTextIndexStat | null>(null);
const pendingWriteContext = ref<FullTextContextSnapshot | null>(null);
let pendingWriteEpoch = -1;
let pendingImportMode: 'insert' | 'replace' = 'insert';
const fullTextFileInput = ref<HTMLInputElement | null>(null);
const importText = ref('');
const importIdPath = ref('_id');
const importMode = ref<'insert' | 'replace'>('insert');
const pendingImportItems = ref<Array<{ id: string; document: unknown }>>([]);
const importSummary = ref('尚未解析文件。');
const importModeOptions: SelectOption[] = [
  { label: 'Insert（重复 ID 报错）', value: 'insert' },
  { label: 'Replace（按 ID 替换）', value: 'replace' },
];

let disposed = false;
let contextEpoch = 0;
let searchRequestId = 0;
let analyzeRequestId = 0;
let documentRequestId = 0;
let searchController: AbortController | null = null;
let analyzeController: AbortController | null = null;
let documentController: AbortController | null = null;
let writeController: AbortController | null = null;

const loadingIndexes = computed(() => props.loading);
const indexes = computed(() => props.indexes);

const activeIndex = computed(() => props.index ?? indexes.value[0] ?? null);
const activeIndexKey = computed(() => activeIndex.value ? indexKey(activeIndex.value) : '');
const activeIndexLabel = computed(() =>
  activeIndex.value ? `${activeIndex.value.collection}.${activeIndex.value.name}` : '');

function liveContext(): FullTextContextSnapshot {
  return {
  connectionId: connections.activeProfile.id,
  connectionName: connections.activeProfile.name,
  endpoint: auth.api.defaults.baseURL ?? '/',
  profileEndpoint: connections.activeProfile.baseUrl,
  database: props.targetDb,
  collection: activeIndex.value?.collection ?? '',
  index: activeIndex.value?.name ?? '',
  authToken: auth.state?.token ?? '',
  schemaSignature: JSON.stringify([activeIndex.value?.fields ?? [], activeIndex.value?.tokenizer ?? '']),
  api: auth.api,
  };
}

const contextSnapshot = computed<FullTextContextSnapshot>(() => liveContext());
const schemaSignature = computed(() => contextSnapshot.value.schemaSignature);

const contextFingerprint = computed(() => [
  contextSnapshot.value.connectionId,
  contextSnapshot.value.endpoint,
  contextSnapshot.value.profileEndpoint,
  contextSnapshot.value.database,
  contextSnapshot.value.collection,
  contextSnapshot.value.index,
].join('\u001f'));

const fulltextState = computed(() => {
  if (permissionDenied.value) return 'permission';
  if (readOnly.value) return 'readonly';
  if (errorMsg.value) return 'error';
  if (!activeIndex.value || (ranOnce.value && hits.value.length === 0)) return 'empty';
  if (hitsTruncated.value || hits.value.length > pageSize.value) return 'longContent';
  return 'normal';
});

const selectedIndexKey = computed({
  get: () => activeIndexKey.value,
  set: (value: string) => {
    const next = indexes.value.find((item) => indexKey(item) === value);
    if (next) selectIndex(next);
  },
});

const indexOptions = computed<SelectOption[]>(() =>
  indexes.value.map((item) => ({
    label: `${item.collection}.${item.name}`,
    value: indexKey(item),
  })));

const fieldOptions = computed<SelectOption[]>(() => {
  const fields = permissionDenied.value ? [] : activeIndex.value?.fields ?? [];
  return [
    { label: 'All indexed fields (*)', value: '*' },
    ...fields.map((value) => ({ label: value, value })),
  ];
});

const effectiveField = computed(() => {
  if (!activeIndex.value) return '*';
  return field.value === '*' || activeIndex.value.fields.includes(field.value) ? field.value : '*';
});

const filteredIndexes = computed(() => {
  const keyword = indexFilter.value.trim().toLowerCase();
  const sorted = [...indexes.value].sort((a, b) => indexKey(a).localeCompare(indexKey(b)));
  if (!keyword) return sorted;
  return sorted.filter((item) =>
    item.collection.toLowerCase().includes(keyword)
    || item.name.toLowerCase().includes(keyword)
    || item.tokenizer.toLowerCase().includes(keyword)
    || item.fields.some((name) => name.toLowerCase().includes(keyword)));
});

const modeOptions: SelectOption[] = [
  { label: 'exact', value: 'exact' },
  { label: 'fuzzy', value: 'fuzzy' },
];

const queryKindOptions: SelectOption[] = [
  { label: 'all terms', value: 'all' },
  { label: 'any term', value: 'any' },
  { label: 'phrase', value: 'phrase' },
];

const pageSizeOptions: SelectOption[] = [
  { label: '10 rows', value: 10 },
  { label: '25 rows', value: 25 },
  { label: '50 rows', value: 50 },
];

const tokenizerOptions = computed<SelectOption[]>(() => {
  const names = new Set(['unicode', 'cjk', 'jieba']);
  if (activeIndex.value?.tokenizer) names.add(activeIndex.value.tokenizer);
  return [...names].map((value) => ({ label: value, value }));
});

const statItems = computed(() => [
  { label: 'Indexes', value: formatStat(indexes.value.length) },
  { label: 'Docs', value: formatStat(activeIndex.value?.documentCount) },
  { label: 'Terms', value: formatStat(activeIndex.value?.termCount) },
  { label: 'Fields', value: formatStat(activeIndex.value?.fields.length) },
  { label: 'Tokenizer', value: activeIndex.value?.tokenizer ?? '-' },
  { label: 'Hits', value: formatStat(hits.value.length) },
]);

const querySummary = computed(() => {
  if (!activeIndex.value) return 'No fulltext index selected.';
  const query = queryText.value.trim();
  if (!query) return 'Query text empty.';
  return `${activeIndexLabel.value} · ${effectiveField.value} · ${mode.value}/${queryKind.value}`;
});

const canSearch = computed(() =>
  Boolean(props.targetDb && activeIndex.value && queryText.value.trim() && !permissionDenied.value));

const highlightTerms = computed(() => {
  const source = searchTokens.value.length > 0 ? searchTokens.value : extractQueryTerms(resultQuery.value);
  return [...new Set(source.map((term) => term.trim()).filter((term) => term.length > 0))]
    .sort((a, b) => b.length - a.length);
});

const hitRows = computed<HitRow[]>(() =>
  hits.value.map((hit, index) => mapHitRow(hit, index)));

const pageCount = computed(() =>
  Math.max(1, Math.ceil(hitRows.value.length / pageSize.value)));

const pagedRows = computed(() => {
  const start = (page.value - 1) * pageSize.value;
  return hitRows.value.slice(start, start + pageSize.value);
});

const selectedHit = computed(() =>
  hitRows.value.find((row) => row.documentId === selectedId.value) ?? hitRows.value[0] ?? null);

const pageSummary = computed(() => {
  if (hitRows.value.length === 0) return 'No hits in the current result.';
  const start = (page.value - 1) * pageSize.value + 1;
  const end = Math.min(hitRows.value.length, start + pageSize.value - 1);
  return `${start}-${end} of ${hitRows.value.length} hits · page ${page.value}/${pageCount.value}`;
});

const resultSummary = computed(() => {
  if (!latestResult.value) return querySummary.value;
  if (latestResult.value.error) return latestResult.value.error.message;
  if (latestResult.value.end) return `${latestResult.value.end.rowCount} rows · ${latestResult.value.end.elapsedMs.toFixed(2)} ms`;
  return 'Ready';
});

const hitColumns = computed<DataTableColumns<HitRow>>(() => [
  {
    title: '#',
    key: 'rank',
    width: 58,
    render: (row) => h('button', {
      type: 'button',
      class: ['fulltext-rank-button', row.documentId === selectedHit.value?.documentId ? 'is-active' : ''],
      onClick: () => { selectedId.value = row.documentId; },
    }, row.rank.toString()),
  },
  {
    title: 'Document',
    key: 'documentId',
    width: 170,
    ellipsis: { tooltip: true },
    render: (row) => h('button', {
      type: 'button',
      class: ['fulltext-doc-button', row.documentId === selectedHit.value?.documentId ? 'is-active' : ''],
      onClick: () => { selectedId.value = row.documentId; },
    }, row.documentId),
  },
  {
    title: 'Score',
    key: 'score',
    width: 116,
    sorter: 'default',
    render: (row) => h('code', formatScore(row.score)),
  },
  {
    title: 'Field',
    key: 'field',
    width: 132,
    ellipsis: { tooltip: true },
    render: (row) => h('code', row.fieldName),
  },
  {
    title: 'Highlight',
    key: 'snippet',
    minWidth: 320,
    render: (row) => h('span', { class: 'fulltext-snippet-cell' }, renderHighlightParts(row.snippetParts)),
  },
]);

function indexKey(index: FullTextIndexStat): string {
  return `fulltext:${index.collection}:${index.name}`;
}

function selectIndex(index: FullTextIndexStat): void {
  emit('selectIndex', index);
}

function captureContext(): FullTextContextSnapshot {
  const snapshot = liveContext();
  const api = createApiClient(() => snapshot.authToken || null);
  api.defaults.baseURL = snapshot.endpoint;
  snapshot.requestApi = api;
  return snapshot;
}

function sameContext(left: FullTextContextSnapshot, right: FullTextContextSnapshot): boolean {
  return left.connectionId === right.connectionId
    && left.endpoint === right.endpoint
    && left.profileEndpoint === right.profileEndpoint
    && left.database === right.database
    && left.collection === right.collection
    && left.index === right.index
    && left.authToken === right.authToken
    && left.schemaSignature === right.schemaSignature
    && left.api === right.api;
}

function isWriteContextCurrent(snapshot: FullTextContextSnapshot, epoch: number): boolean {
  return !disposed && !permissionDenied.value && !readOnly.value
    && contextEpoch === epoch && sameContext(snapshot, liveContext());
}

function isRequestCurrent(
  snapshot: FullTextContextSnapshot,
  epoch: number,
  requestId: number,
  kind: 'search' | 'analyze' | 'document',
): boolean {
  const currentId = kind === 'search' ? searchRequestId
    : kind === 'analyze' ? analyzeRequestId : documentRequestId;
  return !disposed
    && contextEpoch === epoch
    && currentId === requestId
    && sameContext(snapshot, liveContext());
}

function cancelRequests(): void {
  searchController?.abort();
  analyzeController?.abort();
  documentController?.abort();
  writeController?.abort();
  searchController = null;
  analyzeController = null;
  documentController = null;
  writeController = null;
}

function clearReadPayload(): void {
  hits.value = [];
  hitsTruncated.value = false;
  documentsById.value = {};
  selectedId.value = '';
  searchTokens.value = [];
  resultField.value = '*';
  resultQuery.value = '';
  analyzeTokens.value = [];
  latestResult.value = null;
  latestCommand.value = '';
  ranOnce.value = false;
}

function invalidateContext(): void {
  contextEpoch += 1;
  searchRequestId += 1;
  analyzeRequestId += 1;
  documentRequestId += 1;
  cancelRequests();
  searching.value = false;
  analyzing.value = false;
  clearReadPayload();
  pendingRebuild.value = null;
  pendingWriteContext.value = null;
  pendingWriteEpoch = -1;
  pendingImportItems.value = [];
  importText.value = '';
  importSummary.value = '尚未解析文件。';
  previewPlan.value = null;
}

function isAbortError(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const candidate = error as { code?: unknown; name?: unknown; message?: unknown };
  return candidate.code === 'ERR_CANCELED'
    || candidate.name === 'CanceledError'
    || candidate.name === 'AbortError'
    || candidate.message === 'canceled';
}

function isPermissionError(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const status = (error as { response?: { status?: unknown } }).response?.status;
  return status === 401 || status === 403;
}

function isKnownWriteRejection(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const status = (error as { response?: { status?: number } }).response?.status;
  return typeof status === 'number' && status >= 400 && status < 500 && status !== 408;
}

function isRebuildTerminal(response: MaintenanceResponse, context: FullTextContextSnapshot): boolean {
  if (!response || response.operation !== 'rebuild_index'
    || typeof response.completedUtc !== 'string' || !Number.isFinite(Date.parse(response.completedUtc))
    || !Array.isArray(response.checks)) return false;
  if (response.success === false) {
    return response.status === 'failed'
      && response.checks.some((check) => check?.name === 'request' && check.status === 'error');
  }
  const index = response.index;
  return response.success === true && response.status === 'ok'
    && index?.model === 'document' && index.owner === context.collection && index.name === context.index
    && index.kind === 'fulltext' && index.mode === 'sync_touch'
    && index.planned === false && index.rebuildable === true
    && typeof index.documentCount === 'number' && Number.isSafeInteger(index.documentCount) && index.documentCount >= 0
    && response.checks.some((check) => check?.name === 'index' && check.status === 'ok');
}

function lockReadPermission(): void {
  contextEpoch += 1;
  searchRequestId += 1;
  analyzeRequestId += 1;
  documentRequestId += 1;
  cancelRequests();
  searching.value = false;
  analyzing.value = false;
  permissionLocked.value = true;
  clearReadPayload();
  previewPlan.value = null;
  pendingRebuild.value = null;
  pendingWriteContext.value = null;
  pendingWriteEpoch = -1;
  pendingImportItems.value = [];
  importText.value = '';
  importSummary.value = '尚未解析文件。';
  errorMsg.value = '当前身份没有 FullText 读取权限。';
}

async function runSearch(): Promise<void> {
  if (!canSearch.value || !activeIndex.value || permissionDenied.value) return;
  searchController?.abort();
  documentController?.abort();
  analyzeController?.abort();
  documentRequestId += 1;
  analyzeRequestId += 1;
  analyzing.value = false;
  const snapshot = captureContext();
  const epoch = contextEpoch;
  const requestId = ++searchRequestId;
  const controller = new AbortController();
  searchController = controller;
  searching.value = true;
  errorMsg.value = '';
  clearReadPayload();
  const idx = activeIndex.value;
  const request: FullTextSearchPreviewRequest = {
    collection: snapshot.collection,
    index: snapshot.index,
    field: effectiveField.value,
    query: queryText.value.trim(),
    topK: normalizedTopK(),
    mode: mode.value,
    queryKind: queryKind.value,
  };
  const tokenizer = idx.tokenizer;
  const started = performance.now();
  const command = buildCommand(idx, request);
  try {
    const response = await snapshot.requestApi!.post<FullTextSearchPreviewResponse>(
      `/v1/db/${encodeURIComponent(snapshot.database)}/fulltext/search-preview`, request, { signal: controller.signal });
    if (!isRequestCurrent(snapshot, epoch, requestId, 'search')) return;
    if (!Array.isArray(response.data?.hits)) throw new Error('Invalid fulltext response.');
    const serverHits = response.data.hits;
    const resultBudget = Math.min(request.topK ?? 20, FullTextLocalHitBudget);
    const preview = serverHits.slice(0, resultBudget);
    if (!preview.every((hit) => typeof hit.documentId === 'string' && typeof hit.score === 'number' && Number.isFinite(hit.score))) {
      throw new Error('Invalid fulltext hit.');
    }
    hitsTruncated.value = serverHits.length > resultBudget;
    resultField.value = request.field;
    resultQuery.value = request.query;
    hits.value = preview;
    page.value = 1;
    selectedId.value = hits.value[0]?.documentId ?? '';
    await loadHitDocuments(snapshot, epoch, idx.collection, hits.value);
    if (!isRequestCurrent(snapshot, epoch, requestId, 'search')) return;
    await analyzeQueryForHighlight(snapshot, epoch, tokenizer, request.query);
    if (!isRequestCurrent(snapshot, epoch, requestId, 'search')) return;
    const elapsed = performance.now() - started;
    latestCommand.value = command;
    latestResult.value = resultFromHits(hitRows.value, elapsed, hitsTruncated.value);
    ranOnce.value = true;
    recordHistory(
      'success',
      'FullText search preview',
      'search',
      command,
      `${hits.value.length} hits${hitsTruncated.value ? ' · truncated' : ''}`,
      hits.value.length,
      -1,
      elapsed,
      snapshot,
      hitsTruncated.value ? 'truncated' : 'complete',
    );
  } catch (error) {
    if (!isRequestCurrent(snapshot, epoch, requestId, 'search') || isAbortError(error)) return;
    const elapsed = performance.now() - started;
    if (isPermissionError(error)) {
      lockReadPermission();
      recordHistory('error', 'FullText search preview', 'search', command, '当前身份没有 FullText 读取权限。', 0, -1, elapsed, snapshot);
      return;
    }
    const msg = errorToMessage(error, '全文检索失败');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('fulltext_error', msg);
    ranOnce.value = true;
    recordHistory('error', 'FullText search preview', 'search', command, msg, 0, -1, elapsed, snapshot);
  } finally {
    if (isRequestCurrent(snapshot, epoch, requestId, 'search')) searching.value = false;
  }
}

async function loadHitDocuments(
  snapshot: FullTextContextSnapshot,
  epoch: number,
  collection: string,
  items: FullTextSearchPreviewHit[],
): Promise<void> {
  documentsById.value = {};
  const ids = [...new Set(items.map((hit) => hit.documentId).filter(Boolean))];
  if (ids.length === 0) return;
  documentController?.abort();
  const requestId = ++documentRequestId;
  const controller = new AbortController();
  documentController = controller;
  try {
    const response = await snapshot.requestApi!.post<DocumentFindResponse>(
      `/v1/db/${encodeURIComponent(snapshot.database)}/documents/${encodeURIComponent(collection)}/find`, {
    ids,
    limit: Math.min(ids.length, 1000),
      }, { signal: controller.signal });
    if (!isRequestCurrent(snapshot, epoch, requestId, 'document')) return;
    const next: Record<string, DocumentItemResponse> = {};
    if (response.data?.collection !== collection || !Array.isArray(response.data.documents)
      || response.data.documents.length > ids.length
      || !response.data.documents.every((item) => item && typeof item.id === 'string' && ids.includes(item.id)
        && typeof item.version === 'number' && Object.hasOwn(item, 'document'))) {
      throw new Error('Invalid document response.');
    }
    for (const item of response.data.documents) {
      next[item.id] = item;
    }
    documentsById.value = next;
  } catch (error) {
    if (!isRequestCurrent(snapshot, epoch, requestId, 'document') || isAbortError(error)) return;
    if (isPermissionError(error)) {
      lockReadPermission();
      return;
    }
    throw error;
  }
}

async function analyzeQueryForHighlight(
  snapshot: FullTextContextSnapshot,
  epoch: number,
  tokenizer: string,
  query: string,
): Promise<void> {
  analyzeController?.abort();
  const requestId = ++analyzeRequestId;
  const controller = new AbortController();
  analyzeController = controller;
  try {
    const response = await snapshot.requestApi!.post<FullTextAnalyzeResponse>(
      `/v1/db/${encodeURIComponent(snapshot.database)}/fulltext/analyze`, {
      tokenizer,
      text: query,
      }, { signal: controller.signal });
    if (!isRequestCurrent(snapshot, epoch, requestId, 'analyze')) return;
    searchTokens.value = Array.isArray(response.data.tokens) ? response.data.tokens.slice(0, 1000).map((token) => token.text) : [];
  } catch (error) {
    if (!isRequestCurrent(snapshot, epoch, requestId, 'analyze') || isAbortError(error)) return;
    if (isPermissionError(error)) {
      lockReadPermission();
      return;
    }
    searchTokens.value = extractQueryTerms(query);
  }
}

async function runAnalyze(useQuery: boolean): Promise<void> {
  const text = useQuery ? queryText.value.trim() : analyzeText.value;
  if (!text || permissionDenied.value || !props.targetDb || disposed) {
    analyzeTokens.value = [];
    return;
  }
  if (useQuery) analyzeText.value = text;
  analyzeController?.abort();
  const snapshot = captureContext();
  const epoch = contextEpoch;
  const requestId = ++analyzeRequestId;
  const controller = new AbortController();
  const tokenizer = analyzeTokenizer.value;
  analyzeController = controller;
  analyzing.value = true;
  errorMsg.value = '';
  try {
    const response = await snapshot.requestApi!.post<FullTextAnalyzeResponse>(
      `/v1/db/${encodeURIComponent(snapshot.database)}/fulltext/analyze`, {
      tokenizer,
      text,
      }, { signal: controller.signal });
    if (!isRequestCurrent(snapshot, epoch, requestId, 'analyze')) return;
    analyzeTokens.value = Array.isArray(response.data.tokens) ? response.data.tokens.slice(0, 1000) : [];
  } catch (error) {
    if (!isRequestCurrent(snapshot, epoch, requestId, 'analyze') || isAbortError(error)) return;
    if (isPermissionError(error)) {
      lockReadPermission();
      return;
    }
    errorMsg.value = errorToMessage(error, '分词预览失败');
  } finally {
    if (isRequestCurrent(snapshot, epoch, requestId, 'analyze')) analyzing.value = false;
  }
}

function applyBuilder(kind: FullTextQueryKind): void {
  const text = builderText.value.trim();
  if (!text) return;
  queryText.value = text;
  queryKind.value = kind;
  if (kind === 'phrase') mode.value = 'exact';
}

function applyFuzzyBuilder(): void {
  const text = builderText.value.trim();
  if (!text) return;
  queryText.value = text;
  queryKind.value = 'all';
  mode.value = 'fuzzy';
}

function stageRebuild(): void {
  const idx = activeIndex.value;
  if (!idx || disposed || readOnly.value || permissionDenied.value || confirmBusy.value) return;
  pendingImportItems.value = [];
  pendingRebuild.value = { ...idx, fields: [...idx.fields] };
  pendingWriteContext.value = captureContext();
  pendingWriteEpoch = contextEpoch;
  previewPlan.value = createWriteApprovalPlan({
    id: `fulltext_rebuild_${props.targetDb}_${idx.collection}_${idx.name}_${Date.now().toString(36)}`,
    title: 'FullText index rebuild',
    target: `${props.targetDb}.${idx.collection}.${idx.name}`,
    items: [{
      id: `rebuild_${idx.collection}_${idx.name}`,
      command: `rebuild_index document_fulltext ${idx.collection}.${idx.name}`,
      severity: 'write',
      label: 'Derived index rebuild',
      detail: 'Rebuilds the fulltext derived index from document primary data.',
    }],
  });
}

async function confirmPendingWrite(): Promise<void> {
  const stagedContext = pendingWriteContext.value;
  const epoch = pendingWriteEpoch;
  if (confirmBusy.value || !previewPlan.value) return;
  if (!stagedContext || !isWriteContextCurrent(stagedContext, epoch)) {
    clearPreviewPlan();
    errorMsg.value = '审批上下文已变化，请重新暂存当前全文操作。';
    return;
  }
  if (pendingImportItems.value.length > 0) {
    await confirmFullTextImport();
    return;
  }
  const idx = pendingRebuild.value;
  if (!idx) return;
  clearPreviewPlan();
  const controller = new AbortController();
  writeController = controller;
  stagedContext.requestApi!.defaults.signal = controller.signal;
  confirmBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const command = `rebuild_index document_fulltext ${idx.collection}.${idx.name}`;
  try {
    const result = await runMaintenance(stagedContext.requestApi!, stagedContext.database, {
      operation: 'rebuild_index',
      targetModel: 'document_fulltext',
      targetOwner: idx.collection,
      targetName: idx.name,
    });
    const elapsed = performance.now() - started;
    if (!isWriteContextCurrent(stagedContext, epoch)) {
      recordHistory('unknown', 'FullText index rebuild', 'rebuild', command, '请求已发出后上下文变化；请核对原数据库的服务端终态。', 0, -1, elapsed, stagedContext, 'unknown');
      return;
    }
    if (!isRebuildTerminal(result, stagedContext)) throw new Error('Invalid maintenance terminal response.');
    latestCommand.value = command;
    latestResult.value = resultFromMaintenance(result, elapsed);
    ranOnce.value = true;
    previewPlan.value = null;
    pendingRebuild.value = null;
    recordHistory(result.success ? 'success' : 'error', 'FullText index rebuild', 'rebuild', command,
      result.success ? '全文索引重建已完成。' : '全文索引重建未成功。', 0, result.index?.documentCount ?? 0, elapsed, stagedContext, 'complete');
    if (result.success) message.success('全文索引重建已完成。');
    emit('refreshSchema');
  } catch (error) {
    const elapsed = performance.now() - started;
    if (!isWriteContextCurrent(stagedContext, epoch)) {
      recordHistory('unknown', 'FullText index rebuild', 'rebuild', command, '请求已发出后上下文变化；请核对原数据库的服务端终态。', 0, -1, elapsed, stagedContext, 'unknown');
      return;
    }
    if (isPermissionError(error)) {
      lockReadPermission();
      errorMsg.value = '当前身份没有 FullText 写入权限。';
      recordHistory('error', 'FullText index rebuild', 'rebuild', command, '当前身份没有 FullText 写入权限。', 0, 0, elapsed, stagedContext);
      return;
    }
    const knownRejection = isKnownWriteRejection(error);
    const msg = errorToMessage(error, knownRejection ? '全文索引重建失败。' : '全文索引重建结果未知；请核对服务端终态后重新暂存。');
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('fulltext_rebuild_error', msg);
    ranOnce.value = true;
    recordHistory(knownRejection ? 'error' : 'unknown', 'FullText index rebuild', 'rebuild', command, msg, 0, -1, elapsed, stagedContext, knownRejection ? 'complete' : 'unknown');
  } finally {
    if (writeController === controller) writeController = null;
    confirmBusy.value = false;
  }
}

async function onFullTextFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file || readOnly.value || permissionDenied.value || confirmBusy.value || disposed) return;
  const snapshot = captureContext();
  const epoch = contextEpoch;
  try {
    if (file.size > 10 * 1024 * 1024) {
      errorMsg.value = '全文导入文件预览上限为 10 MiB。';
      return;
    }
    const text = await file.text();
    if (!isWriteContextCurrent(snapshot, epoch)) return;
    importText.value = text;
    importSummary.value = `${file.name} · ${(file.size / 1024).toFixed(1)} KiB`;
  } finally {
    input.value = '';
  }
}

function stageFullTextImport(): void {
  const idx = activeIndex.value;
  if (!idx || disposed || readOnly.value || permissionDenied.value || confirmBusy.value) return;
  const parsed = parseFullTextDocuments(importText.value, importIdPath.value.trim() || '_id');
  if (!parsed.ok) {
    errorMsg.value = parsed.message;
    importSummary.value = parsed.message;
    return;
  }
  pendingImportItems.value = parsed.items;
  pendingRebuild.value = null;
  pendingWriteContext.value = captureContext();
  pendingWriteEpoch = contextEpoch;
  pendingImportMode = importMode.value;
  importSummary.value = `${parsed.items.length} documents · ${importMode.value}`;
  previewPlan.value = createWriteApprovalPlan({
    id: `fulltext_import_${props.targetDb}_${idx.collection}_${Date.now().toString(36)}`,
    title: 'FullText document import',
    target: `${props.targetDb}.${idx.collection}`,
    items: [{
      id: `import_${idx.collection}`,
      command: `documents.${importMode.value === 'replace' ? 'replaceMany' : 'insertMany'} ${idx.collection}\n${parsed.items.length} documents`,
      severity: 'write',
      label: 'Import indexed documents',
      detail: `通过 Document API 写入，全文索引 ${idx.name} 随主数据更新。`,
    }],
  });
  errorMsg.value = '';
}

async function confirmFullTextImport(): Promise<void> {
  const idx = activeIndex.value;
  const items = [...pendingImportItems.value];
  const stagedContext = pendingWriteContext.value;
  const epoch = pendingWriteEpoch;
  const stagedMode = pendingImportMode;
  if (confirmBusy.value) return;
  if (!idx || items.length === 0 || !stagedContext || !isWriteContextCurrent(stagedContext, epoch)) {
    clearPreviewPlan();
    errorMsg.value = '审批上下文已变化，请重新暂存当前全文导入。';
    return;
  }
  clearPreviewPlan();
  const controller = new AbortController();
  writeController = controller;
  stagedContext.requestApi!.defaults.signal = controller.signal;
  confirmBusy.value = true;
  errorMsg.value = '';
  const started = performance.now();
  const command = `documents.${stagedMode === 'replace' ? 'replaceMany' : 'insertMany'} ${idx.collection}\n${items.length} documents`;
  const deadline = started + 60_000;
  try {
    const totals: DocumentWriteResponse = { collection: idx.collection, inserted: 0, matched: 0, modified: 0, deleted: 0, errors: null };
    if (stagedMode === 'insert') {
      for (let offset = 0; offset < items.length; offset += 100) {
        if (!isWriteContextCurrent(stagedContext, epoch) || performance.now() >= deadline) {
          recordHistory('unknown', 'FullText document import', 'import', command,
            '已停止后续批次；请核对原数据库已发送请求的服务端终态。', 0, -1, performance.now() - started, stagedContext, 'unknown');
          return;
        }
        const result = await insertManyDocuments(stagedContext.requestApi!, stagedContext.database, stagedContext.collection, {
          documents: items.slice(offset, offset + 100),
          ordered: false,
        });
        validateImportResponse(result, stagedContext.collection);
        totals.inserted += result.inserted ?? 0;
        totals.matched += result.matched ?? 0;
        totals.modified += result.modified ?? 0;
        if (result.errors?.length) totals.errors = [...(totals.errors ?? []), ...result.errors];
      }
    } else {
      for (const item of items) {
        if (!isWriteContextCurrent(stagedContext, epoch) || performance.now() >= deadline) {
          recordHistory('unknown', 'FullText document import', 'import', command,
            '已停止后续批次；请核对原数据库已发送请求的服务端终态。', 0, -1, performance.now() - started, stagedContext, 'unknown');
          return;
        }
        const result = await updateOneDocument(stagedContext.requestApi!, stagedContext.database, stagedContext.collection, item);
        validateImportResponse(result, stagedContext.collection);
        totals.inserted += result.inserted ?? 0;
        totals.matched += result.matched ?? 0;
        totals.modified += result.modified ?? 0;
        if (result.errors?.length) totals.errors = [...(totals.errors ?? []), ...result.errors];
      }
    }
    if (!isWriteContextCurrent(stagedContext, epoch)) {
      recordHistory('unknown', 'FullText document import', 'import', command,
        '请求已发出后上下文变化；请核对原数据库的服务端终态。', 0, -1, performance.now() - started, stagedContext, 'unknown');
      return;
    }
    const elapsed = performance.now() - started;
    const affected = totals.inserted + totals.modified;
    latestCommand.value = command;
    latestResult.value = resultFromDocumentImport(totals, elapsed);
    ranOnce.value = true;
    previewPlan.value = null;
    pendingImportItems.value = [];
    pendingWriteContext.value = null;
    importSummary.value = `${affected}/${items.length} documents written`;
    const completed = !(totals.errors ?? []).some((error) => error.severity !== 'warning')
      && (stagedMode === 'insert' ? totals.inserted === items.length : totals.matched === items.length);
    recordHistory(completed ? 'success' : 'error', 'FullText document import', 'import', command,
      importSummary.value, items.length, affected, elapsed, stagedContext, completed ? 'complete' : 'partial');
    if (completed) message.success(`已写入 ${affected} 个全文文档。`);
    else errorMsg.value = '全文导入仅取得部分写入结果，请核对原数据库；旧审批已消费。';
    emit('refreshSchema');
  } catch (error) {
    const elapsed = performance.now() - started;
    if (!isWriteContextCurrent(stagedContext, epoch)) {
      recordHistory('unknown', 'FullText document import', 'import', command,
        '请求已发出后上下文变化；请核对原数据库的服务端终态。', 0, -1, elapsed, stagedContext, 'unknown');
      return;
    }
    const knownRejection = isKnownWriteRejection(error);
    const msg = isPermissionError(error) ? '当前身份没有 FullText 写入权限。'
      : errorToMessage(error, knownRejection ? '全文数据导入失败。' : '全文数据导入结果未知；请核对服务端终态后重新暂存。');
    if (isPermissionError(error)) {
      lockReadPermission();
      recordHistory('error', 'FullText document import', 'import', command, msg, items.length, -1, elapsed, stagedContext, 'partial');
      return;
    }
    errorMsg.value = msg;
    latestCommand.value = command;
    latestResult.value = errorResult('fulltext_import_error', msg);
    ranOnce.value = true;
    recordHistory(knownRejection ? 'error' : 'unknown', 'FullText document import', 'import', command, msg, items.length, -1, elapsed, stagedContext, knownRejection ? 'partial' : 'unknown');
  } finally {
    if (writeController === controller) writeController = null;
    confirmBusy.value = false;
  }
}

function parseFullTextDocuments(text: string, idPath: string):
  | { ok: true; items: Array<{ id: string; document: unknown }> }
  | { ok: false; message: string } {
  const trimmed = text.trim();
  if (!trimmed) return { ok: false, message: '导入内容为空。' };
  if (text.length > 10 * 1024 * 1024) return { ok: false, message: '全文导入文本预览上限为 10 MiB 字符。' };
  try {
    const source: unknown = trimmed.startsWith('[')
      ? JSON.parse(trimmed) as unknown
      : trimmed.split(/\r?\n/u).filter(Boolean).map((line) => JSON.parse(line) as unknown);
    const documents = Array.isArray(source) ? source : [source];
    if (documents.length > FullTextImportMaxDocuments) throw new Error('单次全文导入上限为 1000 个文档。');
    const seen = new Set<string>();
    const items = documents.map((document, index) => {
      if (!document || typeof document !== 'object' || Array.isArray(document)) {
        throw new Error(`第 ${index + 1} 条记录必须是 JSON 对象。`);
      }
      const rawId = readJsonPath(document, idPath);
      const id = rawId === null || rawId === undefined ? '' : String(rawId).trim();
      if (!id) throw new Error(`第 ${index + 1} 条记录在 ${idPath} 找不到 ID。`);
      if (seen.has(id)) throw new Error(`导入文件包含重复 ID：${id}`);
      seen.add(id);
      return { id, document };
    });
    return { ok: true, items };
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : '全文数据解析失败。' };
  }
}

function validateImportResponse(response: DocumentWriteResponse, collection: string): void {
  if (!response || response.collection !== collection
    || ![response.inserted, response.matched, response.modified, response.deleted]
      .every((count) => Number.isSafeInteger(count) && count >= 0)
    || (response.errors != null && (!Array.isArray(response.errors)
      || response.errors.length > FullTextImportMaxDocuments
      || !response.errors.every((error) => error && typeof error.severity === 'string')))) {
    throw new Error('Invalid document write response.');
  }
}

function resultFromDocumentImport(result: DocumentWriteResponse, elapsedMs: number): SqlResultSet {
  const affected = (result.inserted ?? 0) + (result.modified ?? 0);
  return {
    columns: ['collection', 'inserted', 'matched', 'modified', 'errors'],
    rows: [[result.collection, result.inserted, result.matched, result.modified, result.errors?.length ?? 0]],
    end: { type: 'end', rowCount: 1, recordsAffected: affected, elapsedMs },
    error: null,
    hasColumns: true,
  };
}

function clearFullTextImport(): void {
  importText.value = '';
  importSummary.value = '尚未解析文件。';
  pendingImportItems.value = [];
  pendingWriteContext.value = null;
  if (!pendingRebuild.value) previewPlan.value = null;
}

function clearPreviewPlan(): void {
  previewPlan.value = null;
  pendingRebuild.value = null;
  pendingImportItems.value = [];
  pendingWriteContext.value = null;
}

function openHistoryEntry(entry: WorkbenchHistoryEntry): void {
  latestCommand.value = entry.command;
}

function rowKey(row: HitRow): string {
  return row.key;
}

function mapHitRow(hit: FullTextSearchPreviewHit, index: number): HitRow {
  const doc = documentsById.value[hit.documentId];
  const rawJson = doc ? formatDocument(doc.document) : '';
  const fieldText = doc ? extractFieldText(doc.document, resultField.value) : '';
  const snippet = buildSnippet(fieldText || rawJson, highlightTerms.value);
  return {
    key: `${hit.documentId}:${index}`,
    rank: index + 1,
    documentId: hit.documentId,
    score: hit.score,
    fieldName: resultField.value,
    fieldText,
    snippetText: snippet.text,
    snippetParts: snippet.parts,
    rawJson: rawJson || '(document not loaded)',
    version: doc?.version,
    raw: hit,
  };
}

function renderHighlightParts(parts: HighlightPart[]) {
  return parts.map((part) => part.hit ? h('mark', { class: 'fulltext-mark' }, part.text) : part.text);
}

function buildSnippet(text: string, terms: string[]): { text: string; parts: HighlightPart[] } {
  const normalized = text.replace(/\s+/g, ' ').trim();
  if (!normalized) return { text: '-', parts: [{ text: '-', hit: false }] };
  const lower = normalized.toLowerCase();
  const lowerTerms = terms.map((term) => term.toLowerCase()).filter(Boolean);
  const first = lowerTerms.reduce((best, term) => {
    const index = lower.indexOf(term);
    return index >= 0 && index < best ? index : best;
  }, Number.POSITIVE_INFINITY);
  const center = Number.isFinite(first) ? first : 0;
  const start = Math.max(0, center - 90);
  const end = Math.min(normalized.length, start + 240);
  const snippet = `${start > 0 ? '...' : ''}${normalized.slice(start, end)}${end < normalized.length ? '...' : ''}`;
  return {
    text: snippet,
    parts: highlightText(snippet, lowerTerms),
  };
}

function highlightText(text: string, lowerTerms: string[]): HighlightPart[] {
  const ranges: Array<{ start: number; end: number }> = [];
  const lower = text.toLowerCase();
  for (const term of lowerTerms) {
    if (!term) continue;
    let start = lower.indexOf(term);
    while (start >= 0) {
      ranges.push({ start, end: start + term.length });
      start = lower.indexOf(term, start + Math.max(1, term.length));
    }
  }
  if (ranges.length === 0) return [{ text, hit: false }];

  ranges.sort((a, b) => a.start - b.start || b.end - a.end);
  const merged: Array<{ start: number; end: number }> = [];
  for (const range of ranges) {
    const last = merged[merged.length - 1];
    if (last && range.start <= last.end) {
      last.end = Math.max(last.end, range.end);
    } else {
      merged.push({ ...range });
    }
  }

  const parts: HighlightPart[] = [];
  let cursor = 0;
  for (const range of merged) {
    if (range.start > cursor) parts.push({ text: text.slice(cursor, range.start), hit: false });
    parts.push({ text: text.slice(range.start, range.end), hit: true });
    cursor = range.end;
  }
  if (cursor < text.length) parts.push({ text: text.slice(cursor), hit: false });
  return parts;
}

function extractQueryTerms(text: string): string[] {
  return text
    .replace(/\b(AND|OR|NOT)\b/gi, ' ')
    .replace(/["'()]/g, ' ')
    .split(/[^\p{L}\p{N}_]+/u)
    .map((term) => term.trim())
    .filter(Boolean);
}

function extractFieldText(document: unknown, selectedField: string): string {
  if (selectedField === '*' || selectedField === 'document' || selectedField === 'json') {
    return formatDocument(document);
  }
  const value = readJsonPath(document, selectedField);
  return value === undefined ? formatDocument(document) : formatDocument(value);
}

function readJsonPath(source: unknown, path: string): unknown {
  const normalized = path.startsWith('$.') ? path.slice(2) : path.replace(/^\$\.?/u, '');
  if (!normalized) return undefined;
  const parts = normalized.split('.').filter(Boolean);
  let current: unknown = source;
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

function formatDocument(value: unknown): string {
  if (typeof value === 'string') return value;
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value ?? '');
  }
}

function resultFromHits(rows: HitRow[], elapsedMs: number, truncated: boolean): SqlResultSet {
  return {
    columns: ['rank', 'document_id', 'score', 'field', 'snippet'],
    rows: rows.map((row) => [row.rank, row.documentId, row.score, row.fieldName, row.snippetText]),
    end: {
      type: 'end',
      rowCount: rows.length,
      recordsAffected: -1,
      elapsedMs,
      truncated,
    },
    error: null,
    hasColumns: true,
  };
}

function resultFromMaintenance(result: MaintenanceResponse, elapsedMs: number): SqlResultSet {
  return {
    columns: ['operation', 'status', 'success', 'message', 'documents'],
    rows: [[result.operation, result.status, result.success,
      result.success ? '全文索引重建已完成。' : '全文索引重建未成功。', result.index?.documentCount ?? null]],
    end: {
      type: 'end',
      rowCount: 1,
      recordsAffected: result.index?.documentCount ?? -1,
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

function normalizedTopK(): number {
  const value = topK.value;
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(1, Math.min(FullTextLocalHitBudget, Math.floor(value))) : 20;
}

function buildCommand(index: FullTextIndexStat, request: FullTextSearchPreviewRequest): string {
  const fieldArg = request.field === '*' ? '*' : quote(request.field);
  return [
    `SELECT id, score, document`,
    `FROM ${formatSqlIdentifier(index.collection)}`,
    `WHERE match(${formatSqlIdentifier(index.name)}, ${fieldArg}, ${quote(request.query)}, ${request.topK}, ${quote(request.mode ?? 'exact')})`,
    `-- queryKind: ${request.queryKind}`,
  ].join('\n');
}

function recordHistory(
  status: 'success' | 'error' | 'unknown',
  title: string,
  action: string,
  command: string,
  summary: string,
  rowCount: number,
  recordsAffected: number,
  elapsedMs: number,
  context: FullTextContextSnapshot = captureContext(),
  completeness?: WorkbenchHistoryCompleteness,
): void {
  history.record({
    kind: action === 'search' ? 'query' : 'operation',
    status,
    title,
    target: context.collection && context.index ? `${context.collection}.${context.index}` : activeIndexLabel.value,
    database: context.database,
    connectionId: context.connectionId,
    connectionName: context.connectionName,
    model: 'fulltext',
    action,
    command,
    summary,
    rowCount,
    recordsAffected,
    elapsedMs,
    completeness,
  });
}

async function copyText(text: string, success: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    message.success(success);
  } catch {
    message.warning(text);
  }
}

function formatScore(value?: number | null): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '-';
  return Math.abs(value) < 0.000001 ? value.toExponential(3) : value.toFixed(6);
}

function formatStat(value?: number | null): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString() : '-';
}

function errorToMessage(error: unknown, fallback: string): string {
  if (isPermissionError(error)) return '当前身份没有 FullText 读取权限。';
  return fallback;
}

watch(activeIndex, (index) => {
  invalidateContext();
  field.value = '*';
  analyzeTokenizer.value = index?.tokenizer || 'unicode';
}, { immediate: true, flush: 'sync' });

watch(queryKind, (kind) => {
  if (kind === 'phrase' && mode.value === 'fuzzy') {
    mode.value = 'exact';
  }
});

watch(mode, (value) => {
  if (value === 'fuzzy' && queryKind.value === 'phrase') {
    queryKind.value = 'all';
  }
});

watch([hitRows, pageSize], () => {
  if (page.value > pageCount.value) page.value = pageCount.value;
});

watch(contextFingerprint, () => {
  invalidateContext();
  permissionLocked.value = false;
  errorMsg.value = '';
}, { immediate: true, flush: 'sync' });

watch([() => auth.state, () => auth.state?.token, () => auth.api], () => {
  invalidateContext();
}, { flush: 'sync' });

watch(schemaSignature, () => {
  invalidateContext();
}, { flush: 'sync' });

watch(() => props.permissionDenied, () => {
  invalidateContext();
}, { flush: 'sync' });

watch(readOnly, () => {
  contextEpoch += 1;
  cancelRequests();
  searching.value = false;
  analyzing.value = false;
  clearPreviewPlan();
}, { flush: 'sync' });

onBeforeUnmount(() => {
  disposed = true;
  invalidateContext();
});
</script>

<style scoped>
.fulltext-workbench {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #fff;
}

.fulltext-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbf8ff;
}

.fulltext-toolbar__identity {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.fulltext-toolbar__title {
  color: var(--sndb-ink-strong);
  font-size: 15px;
  font-weight: 800;
}

.fulltext-toolbar__meta,
.fulltext-panel-head__meta {
  font-size: 12px;
}

.fulltext-toolbar__actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.fulltext-toolbar__index {
  width: 230px;
}

.fulltext-toolbar__field {
  width: 180px;
}

.fulltext-toolbar__topk {
  width: 86px;
}

.fulltext-alert {
  margin: 10px 12px 0;
}

.fulltext-stats {
  display: grid;
  grid-template-columns: repeat(6, minmax(110px, 1fr));
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.fulltext-stat {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  padding: 9px 12px;
  border-right: 1px solid rgba(15, 23, 42, 0.08);
}

.fulltext-stat span,
.fulltext-param-strip small,
.fulltext-token-list small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.fulltext-stat strong,
.fulltext-param-strip strong {
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-size: 15px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-body {
  display: grid;
  flex: 1;
  min-height: 430px;
  grid-template-columns: 250px minmax(440px, 1fr) 360px;
  min-width: 0;
  overflow: hidden;
}

.fulltext-body.is-analyzer {
  grid-template-columns: minmax(460px, 760px);
  justify-content: center;
  padding: 20px;
  overflow: auto;
  background: var(--sndb-surface);
}

.fulltext-body.is-index {
  grid-template-columns: 300px minmax(420px, 720px);
  justify-content: center;
  padding: 20px;
  overflow: auto;
  background: var(--sndb-surface);
}

.fulltext-body.is-import {
  grid-template-columns: 250px minmax(460px, 1fr) 320px;
}

.fulltext-import-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  padding: 14px;
  overflow: auto;
  border-right: 1px solid var(--sndb-border);
}

.fulltext-import-options,
.fulltext-import-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.fulltext-import-options > :first-child,
.fulltext-import-options > :nth-child(2) {
  width: min(260px, 40%);
}

.fulltext-file-input {
  display: none;
}

.fulltext-body.is-analyzer .fulltext-inspector,
.fulltext-body.is-index .fulltext-indexes,
.fulltext-body.is-index .fulltext-inspector {
  border: 1px solid var(--sndb-border);
  background: #fff;
}

.fulltext-index-detail {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 16px;
}

.fulltext-index-detail dl {
  display: grid;
  gap: 0;
  margin: 0;
  border: 1px solid var(--sndb-border);
}

.fulltext-index-detail dl div {
  display: grid;
  grid-template-columns: 120px minmax(0, 1fr);
  gap: 12px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--sndb-border);
}

.fulltext-index-detail dl div:last-child {
  border-bottom: 0;
}

.fulltext-index-detail dt {
  color: var(--sndb-ink-muted);
}

.fulltext-index-detail dd {
  margin: 0;
  font-family: "Cascadia Code", Consolas, monospace;
}

.fulltext-indexes,
.fulltext-search-panel,
.fulltext-inspector {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.fulltext-indexes {
  border-right: 1px solid rgba(15, 23, 42, 0.08);
  background: #fbfcfe;
}

.fulltext-inspector {
  border-left: 1px solid rgba(15, 23, 42, 0.08);
  background: #fffdfa;
}

.fulltext-panel-head {
  display: flex;
  flex: 0 0 auto;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.fulltext-panel-head--grid {
  align-items: center;
}

.fulltext-panel-head__title,
.fulltext-section-title {
  display: block;
  color: var(--sndb-ink-strong);
  font-weight: 800;
}

.fulltext-index-filter {
  flex: 0 0 auto;
  margin: 8px;
  width: calc(100% - 16px);
}

.fulltext-index-list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
  overflow: auto;
  padding: 0 8px 8px;
}

.fulltext-index-card,
.fulltext-rank-button,
.fulltext-doc-button {
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.fulltext-index-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  width: 100%;
  min-width: 0;
  padding: 8px;
  border-left: 2px solid rgba(131, 86, 210, 0.45);
  border-radius: 6px;
  text-align: left;
}

.fulltext-index-card:hover,
.fulltext-index-card.is-active,
.fulltext-rank-button:hover,
.fulltext-rank-button.is-active,
.fulltext-doc-button:hover,
.fulltext-doc-button.is-active {
  background: rgba(131, 86, 210, 0.09);
}

.fulltext-index-card.is-active {
  border-left-color: rgba(131, 86, 210, 0.9);
}

.fulltext-index-card span {
  width: 100%;
  overflow: hidden;
  color: var(--sndb-ink-strong);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-index-card small {
  color: var(--sndb-ink-soft);
  font-size: 11px;
}

.fulltext-query-editor {
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
  background: #fff;
}

.fulltext-builder {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
}

.fulltext-mode-select,
.fulltext-kind-select {
  width: 118px;
}

.fulltext-param-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 8px;
}

.fulltext-param-strip span {
  min-width: 0;
  padding: 7px 8px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.fulltext-param-strip small,
.fulltext-param-strip strong {
  display: block;
}

.fulltext-grid {
  flex: 1;
  min-height: 0;
}

.fulltext-grid :deep(.n-data-table-base-table-body) {
  min-height: 260px;
}

.fulltext-rank-button {
  min-width: 30px;
  padding: 2px 5px;
  border-radius: 4px;
  color: #6f49b8;
  font-family: "SFMono-Regular", "Cascadia Code", Consolas, monospace;
  font-size: 12px;
  font-weight: 800;
}

.fulltext-doc-button {
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

.fulltext-snippet-cell {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  color: #345;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.fulltext-mark,
.fulltext-snippet mark {
  border-radius: 3px;
  background: rgba(247, 196, 83, 0.55);
  color: #1f2937;
  font-weight: 800;
}

.fulltext-pager {
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

.fulltext-page-size {
  width: 104px;
}

.fulltext-detail-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 9px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.08);
}

.fulltext-detail-strip span {
  padding: 2px 7px;
  border-radius: 999px;
  background: rgba(131, 86, 210, 0.08);
  color: var(--sndb-ink-soft);
  font-size: 11px;
  font-weight: 700;
}

.fulltext-section,
.fulltext-analyzer {
  padding: 10px 12px 0;
}

.fulltext-section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 6px;
}

.fulltext-section-title--standalone {
  margin-bottom: 8px;
}

.fulltext-snippet {
  min-height: 54px;
  margin: 0;
  padding: 10px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
  color: #24384f;
  font-size: 13px;
  line-height: 1.55;
}

.fulltext-document pre {
  max-height: 210px;
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

.fulltext-analyzer {
  display: flex;
  flex-direction: column;
  gap: 8px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
  margin-top: 10px;
  padding-bottom: 12px;
}

.fulltext-analyzer-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  gap: 8px;
  align-items: center;
}

.fulltext-token-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  min-height: 40px;
}

.fulltext-token-list span {
  display: inline-flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  padding: 5px 7px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fbfcfe;
}

.fulltext-token-list strong {
  color: var(--sndb-ink-strong);
  font-size: 12px;
}

.fulltext-result {
  flex: 0 0 240px;
  min-height: 220px;
  border-top: 1px solid rgba(15, 23, 42, 0.08);
}

@media (max-width: 1360px) {
  .fulltext-body {
    grid-template-columns: 230px minmax(420px, 1fr);
  }

  .fulltext-inspector {
    grid-column: 1 / -1;
    border-top: 1px solid rgba(15, 23, 42, 0.08);
    border-left: 0;
  }
}

@media (max-width: 980px) {
  .fulltext-toolbar,
  .fulltext-panel-head--grid,
  .fulltext-pager {
    flex-direction: column;
    align-items: stretch;
  }

  .fulltext-body {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .fulltext-indexes,
  .fulltext-inspector {
    border-right: 0;
    border-left: 0;
  }

  .fulltext-stats,
  .fulltext-param-strip,
  .fulltext-builder,
  .fulltext-analyzer-row {
    grid-template-columns: 1fr;
  }

  .fulltext-toolbar__index,
  .fulltext-toolbar__field,
  .fulltext-toolbar__topk,
  .fulltext-mode-select,
  .fulltext-kind-select,
  .fulltext-page-size {
    width: 100%;
  }
}
</style>
