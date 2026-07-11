<template>
  <section class="advanced-workbench" :data-testid="`document-${mode}`">
    <WriteApprovalPanel
      v-if="approvalPlan"
      :plan="approvalPlan"
      :busy="writeBusy"
      @cancel="clearPending"
      @confirm="confirmPending"
    />

    <n-alert v-if="errorMsg" type="error" :title="errorMsg" closable @close="errorMsg = ''" />

    <template v-if="mode === 'update'">
      <header class="advanced-head">
        <div>
          <n-text class="advanced-title">局部更新</n-text>
          <n-text depth="3">同一服务端执行器生成预览和提交结果</n-text>
        </div>
        <n-space align="center" size="small">
          <n-switch v-model:value="updateMany" size="small" />
          <n-text depth="3">更新全部匹配项</n-text>
          <n-switch v-model:value="updateUpsert" size="small" />
          <n-text depth="3">Upsert</n-text>
        </n-space>
      </header>

      <div class="editor-pair">
        <label class="field-block">
          <span>Filter</span>
          <n-input v-model:value="updateFilterText" type="textarea" :autosize="{ minRows: 6, maxRows: 12 }" />
        </label>
        <label class="field-block">
          <span>Update operators</span>
          <n-input v-model:value="updateText" type="textarea" :autosize="{ minRows: 6, maxRows: 12 }" />
        </label>
      </div>

      <div class="action-bar">
        <n-input-number v-model:value="previewLimit" :min="1" :max="100" size="small" class="short-control" />
        <n-input v-if="updateUpsert" v-model:value="upsertId" size="small" placeholder="Upsert document ID" class="medium-control" />
        <n-button type="primary" size="small" :loading="readBusy" :disabled="!collection" @click="runUpdatePreview">
          <template #icon><Eye :size="16" /></template>
          生成预览
        </n-button>
        <n-button size="small" secondary :disabled="previewItems.length === 0 || !previewRequest" @click="stageUpdate">
          <template #icon><ShieldCheck :size="16" /></template>
          暂存提交
        </n-button>
        <n-tag v-if="previewItems.length > 0" size="small" :bordered="false">
          {{ previewItems.length }} matched · {{ changedPreviewCount }} changed
        </n-tag>
      </div>

      <div v-if="selectedPreview" class="diff-shell">
        <aside class="diff-list">
          <button
            v-for="item in previewItems"
            :key="item.id"
            type="button"
            :class="{ 'is-active': item.id === selectedPreview.id }"
            @click="selectedPreviewId = item.id"
          >
            <strong>{{ item.id }}</strong>
            <span>v{{ item.version }} · {{ item.isUpsert ? 'upsert' : item.changed ? 'changed' : 'unchanged' }}</span>
          </button>
        </aside>
        <div class="diff-grid">
          <article>
            <header>Before</header>
            <pre>{{ formatJson(selectedPreview.before) }}</pre>
          </article>
          <article>
            <header>After</header>
            <pre>{{ formatJson(selectedPreview.after) }}</pre>
          </article>
        </div>
      </div>
      <n-empty v-else description="生成预览后可核对每条文档的前后差异。" />
    </template>

    <template v-else-if="mode === 'indexes'">
      <header class="advanced-head">
        <div>
          <n-text class="advanced-title">索引设计器</n-text>
          <n-text depth="3">复合、Unique、Sparse、Partial 与 TTL 索引</n-text>
        </div>
        <n-button size="small" secondary :loading="readBusy" :disabled="!collection" @click="validateIndexes">
          <template #icon><ShieldCheck :size="16" /></template>
          校验一致性
        </n-button>
      </header>

      <section class="index-designer">
        <div class="index-primary-row">
          <n-input v-model:value="indexName" size="small" placeholder="Index name" />
          <n-input v-model:value="indexPathsText" size="small" placeholder="$.site, $.serial" />
          <n-button type="primary" size="small" :disabled="!canStageIndex" @click="stageCreateIndex">
            <template #icon><Plus :size="16" /></template>
            暂存创建
          </n-button>
        </div>
        <div class="option-strip">
          <label><n-switch v-model:value="indexUnique" size="small" /><span>Unique</span></label>
          <label><n-switch v-model:value="indexSparse" size="small" /><span>Sparse</span></label>
          <label><n-switch v-model:value="indexPartial" size="small" /><span>Partial</span></label>
          <label><n-switch v-model:value="indexTtl" size="small" /><span>TTL</span></label>
        </div>
        <div v-if="indexPartial" class="conditional-row">
          <n-input v-model:value="partialPath" size="small" placeholder="Partial path, e.g. $.active" />
          <n-select v-model:value="partialOperator" size="small" :options="partialOperatorOptions" />
          <n-input v-if="partialOperator !== 'exists'" v-model:value="partialValue" size="small" placeholder="Stable scalar value" />
        </div>
        <div v-if="indexTtl" class="conditional-row conditional-row--ttl">
          <n-input v-model:value="ttlPath" size="small" placeholder="UTC date path" />
          <n-input-number v-model:value="ttlSeconds" size="small" :min="1" placeholder="Seconds" />
        </div>
      </section>

      <div v-if="consistency" class="consistency-strip" :class="{ 'is-error': !consistency.isConsistent }">
        <strong>{{ consistency.isConsistent ? '索引一致' : '发现索引不一致' }}</strong>
        <span>{{ consistency.documentCount }} documents · {{ consistency.indexes.length }} indexes</span>
      </div>

      <div class="index-table-wrap">
        <n-data-table
          :columns="indexColumns"
          :data="collection?.jsonIndexes ?? []"
          :bordered="false"
          :single-line="false"
          :pagination="false"
          size="small"
        />
      </div>
    </template>

    <template v-else>
      <header class="advanced-head">
        <div>
          <n-space align="center" size="small">
            <span class="live-dot" :class="{ 'is-live': feedActive }" />
            <n-text class="advanced-title">Change Feed</n-text>
          </n-space>
          <n-text depth="3">collection 级持久化变更 · 7 天保留 · resume token 续传</n-text>
        </div>
        <n-space size="small">
          <n-button v-if="!feedActive" type="primary" size="small" :disabled="!collection" @click="startFeed">
            <template #icon><Play :size="16" /></template>
            开始监听
          </n-button>
          <n-button v-else size="small" secondary @click="pauseFeed">
            <template #icon><Pause :size="16" /></template>
            暂停
          </n-button>
          <n-button size="small" quaternary :disabled="feedItems.length === 0" @click="clearFeed">
            <template #icon><Trash2 :size="16" /></template>
            清空
          </n-button>
        </n-space>
      </header>

      <div class="feed-controls">
        <n-select v-model:value="feedStartAt" size="small" :options="feedStartOptions" />
        <n-select v-model:value="feedOperations" size="small" multiple :options="feedOperationOptions" placeholder="All operations" />
        <n-input v-model:value="feedDocumentId" size="small" clearable placeholder="Document ID filter" />
        <n-select v-model:value="feedInterval" size="small" :options="feedIntervalOptions" />
        <n-button size="small" secondary :loading="feedBusy" :disabled="!collection" title="立即读取" @click="pollFeed">
          <template #icon><RefreshCw :size="16" /></template>
        </n-button>
      </div>

      <div class="feed-status">
        <span>{{ feedItems.length }} events</span>
        <span>latest #{{ latestSequence }}</span>
        <span v-if="resumeExpiresAt">token {{ formatTime(resumeExpiresAt) }} expires</span>
        <span v-if="feedLastPollAt">updated {{ formatTime(feedLastPollAt) }}</span>
      </div>

      <div class="feed-layout">
        <div class="feed-list">
          <button
            v-for="item in feedItems"
            :key="item.sequence"
            type="button"
            :class="['feed-event', `is-${item.operation}`, { 'is-active': item.sequence === selectedFeed?.sequence }]"
            @click="selectedFeedSequence = item.sequence"
          >
            <span class="feed-event__seq">#{{ item.sequence }}</span>
            <n-tag size="tiny" :type="operationTagType(item.operation)" :bordered="false">{{ item.operation }}</n-tag>
            <strong>{{ item.documentId }}</strong>
            <time>{{ formatTime(item.occurredAtUtc) }}</time>
          </button>
          <n-empty v-if="feedItems.length === 0" description="监听已开启时，新变更会出现在这里。" />
        </div>
        <div v-if="selectedFeed" class="feed-detail">
          <div class="feed-detail__meta">
            <n-tag size="small" :type="operationTagType(selectedFeed.operation)" :bordered="false">{{ selectedFeed.operation }}</n-tag>
            <code>{{ selectedFeed.documentId }}</code>
            <span>v{{ selectedFeed.documentVersion }}</span>
            <n-tag v-if="selectedFeed.payloadTruncated" size="tiny" type="warning" :bordered="false">payload truncated</n-tag>
          </div>
          <div class="diff-grid">
            <article><header>Before</header><pre>{{ formatJson(selectedFeed.before) }}</pre></article>
            <article><header>After</header><pre>{{ formatJson(selectedFeed.after) }}</pre></article>
          </div>
        </div>
        <n-empty v-else description="选择一条变更查看前后镜像。" />
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, h, onBeforeUnmount, ref, watch } from 'vue';
import {
  NAlert,
  NButton,
  NDataTable,
  NEmpty,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NSwitch,
  NTag,
  NText,
  useMessage,
  type DataTableColumns,
  type SelectOption,
} from 'naive-ui';
import { Eye, Pause, Play, Plus, RefreshCw, ShieldCheck, Trash2 } from 'lucide-vue-next';
import {
  createDocumentIndex,
  dropDocumentIndex,
  previewDocumentUpdate,
  readDocumentChangeFeed,
  updateManyDocuments,
  updateOneDocument,
  validateDocumentIndexes,
  type DocumentChangeFeedItem,
  type DocumentFilter,
  type DocumentIndexConsistencyResponse,
  type DocumentUpdateContract,
  type DocumentUpdatePreviewItem,
  type DocumentUpdatePreviewRequest,
} from '@/api/documents';
import { runMaintenance, type DocumentCollectionInfo, type DocumentJsonIndexInfo } from '@/api/schema';
import WriteApprovalPanel from '@/components/WriteApprovalPanel.vue';
import { useAuthStore } from '@/stores/auth';
import { useConnectionsStore } from '@/stores/connections';
import { useWorkbenchHistoryStore } from '@/stores/workbenchHistory';
import { createWriteApprovalPlan, type WriteApprovalPlan, type WriteApprovalSeverity } from '@/utils/writeApproval';

type AdvancedMode = 'update' | 'indexes' | 'changeFeed';
type FeedOperation = 'insert' | 'update' | 'delete';

const props = defineProps<{
  mode: AdvancedMode;
  targetDb: string;
  collection: DocumentCollectionInfo | null;
}>();

const emit = defineEmits<{ refreshSchema: []; refreshDocuments: [] }>();
const auth = useAuthStore();
const connections = useConnectionsStore();
const history = useWorkbenchHistoryStore();
const message = useMessage();

const errorMsg = ref('');
const readBusy = ref(false);
const writeBusy = ref(false);
const pending = ref<null | { label: string; detail: string; command: string; severity: WriteApprovalSeverity; run: () => Promise<number> }>(null);

const updateMany = ref(false);
const updateUpsert = ref(false);
const upsertId = ref('');
const previewLimit = ref<number | null>(20);
const updateFilterText = ref('{\n  "path": "$.site",\n  "op": "eq",\n  "value": "north"\n}');
const updateText = ref('{\n  "set": { "$.status": "active" },\n  "inc": { "$.revision": 1 }\n}');
const previewItems = ref<DocumentUpdatePreviewItem[]>([]);
const previewRequest = ref<DocumentUpdatePreviewRequest | null>(null);
const selectedPreviewId = ref('');
const selectedPreview = computed(() => previewItems.value.find((item) => item.id === selectedPreviewId.value) ?? previewItems.value[0] ?? null);
const changedPreviewCount = computed(() => previewItems.value.filter((item) => item.changed).length);

const indexName = ref('');
const indexPathsText = ref('$.site');
const indexUnique = ref(false);
const indexSparse = ref(false);
const indexPartial = ref(false);
const indexTtl = ref(false);
const partialPath = ref('$.active');
const partialOperator = ref('exists');
const partialValue = ref('true');
const ttlPath = ref('$.expiresAt');
const ttlSeconds = ref<number | null>(86400);
const consistency = ref<DocumentIndexConsistencyResponse | null>(null);

const feedActive = ref(false);
const feedBusy = ref(false);
const feedStartAt = ref<'now' | 'beginning'>('now');
const feedOperations = ref<FeedOperation[]>([]);
const feedDocumentId = ref('');
const feedInterval = ref(2000);
const feedItems = ref<DocumentChangeFeedItem[]>([]);
const feedResumeToken = ref('');
const feedLastPollAt = ref('');
const resumeExpiresAt = ref('');
const latestSequence = ref(0);
const selectedFeedSequence = ref(0);
let feedTimer: number | undefined;

const selectedFeed = computed(() => feedItems.value.find((item) => item.sequence === selectedFeedSequence.value) ?? feedItems.value[0] ?? null);
const canStageIndex = computed(() => Boolean(props.collection && indexName.value.trim() && parsePaths().length > 0));

const partialOperatorOptions: SelectOption[] = [
  { label: 'Exists', value: 'exists' }, { label: 'Equal', value: 'eq' }, { label: 'Not equal', value: 'ne' },
  { label: 'Greater than', value: 'gt' }, { label: 'Greater or equal', value: 'gte' },
  { label: 'Less than', value: 'lt' }, { label: 'Less or equal', value: 'lte' },
];
const feedStartOptions: SelectOption[] = [{ label: '从现在开始', value: 'now' }, { label: '读取保留窗口', value: 'beginning' }];
const feedOperationOptions: SelectOption[] = [{ label: 'Insert', value: 'insert' }, { label: 'Update', value: 'update' }, { label: 'Delete', value: 'delete' }];
const feedIntervalOptions: SelectOption[] = [{ label: '1 秒', value: 1000 }, { label: '2 秒', value: 2000 }, { label: '5 秒', value: 5000 }, { label: '10 秒', value: 10000 }];

const approvalPlan = computed<WriteApprovalPlan | null>(() => pending.value ? createWriteApprovalPlan({
  id: `document_advanced_${Date.now()}`,
  title: pending.value.label,
  target: `${props.targetDb}.${props.collection?.name ?? ''}`,
  items: [{ id: 'advanced-write', command: pending.value.command, severity: pending.value.severity, label: pending.value.label, detail: pending.value.detail }],
}) : null);

const indexColumns = computed<DataTableColumns<DocumentJsonIndexInfo>>(() => [
  { title: 'Name', key: 'name', minWidth: 150, ellipsis: { tooltip: true } },
  { title: 'Paths', key: 'paths', minWidth: 220, render: (row) => h('code', (row.paths ?? [row.path]).join(', ')) },
  { title: 'Mode', key: 'mode', minWidth: 180, render: (row) => h('span', indexMode(row)) },
  { title: 'TTL', key: 'ttl', width: 100, render: (row) => row.isTtl ? `${row.ttlSeconds}s` : '-' },
  {
    title: '', key: 'actions', width: 170, render: (row) => h(NSpace, { size: 4, wrap: false }, {
      default: () => [
        h(NButton, { size: 'tiny', secondary: true, onClick: () => stageRebuildIndex(row.name) }, { default: () => 'Rebuild' }),
        h(NButton, { size: 'tiny', tertiary: true, type: 'error', onClick: () => stageDropIndex(row.name) }, { default: () => 'Drop' }),
      ],
    }),
  },
]);

async function runUpdatePreview(): Promise<void> {
  if (!props.collection) return;
  const filter = parseJson<DocumentFilter>(updateFilterText.value, 'Filter 必须是 JSON 对象。');
  const update = parseJson<DocumentUpdateContract>(updateText.value, 'Update operators 必须是 JSON 对象。');
  if (!filter.ok) { errorMsg.value = filter.message; return; }
  if (!update.ok) { errorMsg.value = update.message; return; }
  const request: DocumentUpdatePreviewRequest = {
    filter: filter.value,
    update: update.value,
    many: updateMany.value,
    limit: previewLimit.value ?? 20,
    upsert: updateUpsert.value,
    upsertId: updateUpsert.value ? upsertId.value.trim() || null : null,
  };
  readBusy.value = true; errorMsg.value = '';
  try {
    const response = await previewDocumentUpdate(auth.api, props.targetDb, props.collection.name, request);
    previewItems.value = response.documents;
    previewRequest.value = request;
    selectedPreviewId.value = response.documents[0]?.id ?? '';
    recordHistory('success', 'Document update preview', 'update_preview', JSON.stringify(request), `${response.changed}/${response.matched} changed`, response.matched, 0);
  } catch (error) { handleError(error, '生成更新预览失败', 'update_preview'); }
  finally { readBusy.value = false; }
}

function stageUpdate(): void {
  if (!props.collection || !previewRequest.value) return;
  const request = previewRequest.value;
  pending.value = {
    label: request.many ? '更新匹配文档' : '更新首个匹配文档',
    detail: `${previewItems.value.length} matched · ${changedPreviewCount.value} changed`,
    severity: request.many ? 'danger' : 'write',
    command: `${request.many ? 'updateMany' : 'updateOne'} ${props.collection.name}\n${JSON.stringify(request, null, 2)}`,
    run: async () => {
      const response = request.many
        ? await updateManyDocuments(auth.api, props.targetDb, props.collection!.name, request)
        : await updateOneDocument(auth.api, props.targetDb, props.collection!.name, request);
      return response.inserted + response.modified;
    },
  };
}

function stageCreateIndex(): void {
  if (!props.collection || !canStageIndex.value) return;
  const request = {
    name: indexName.value.trim(), paths: parsePaths(), isUnique: indexUnique.value, isSparse: indexSparse.value,
    partialFilter: indexPartial.value ? { path: partialPath.value.trim(), operator: partialOperator.value, valueScalar: partialOperator.value === 'exists' ? null : partialValue.value } : null,
    ttlPath: indexTtl.value ? ttlPath.value.trim() : null, ttlSeconds: indexTtl.value ? ttlSeconds.value : null,
  };
  pending.value = {
    label: '创建 Document 索引', detail: `${request.paths.join(' + ')} · ${indexMode(request)}`, severity: 'write',
    command: `createIndex ${props.collection.name}\n${JSON.stringify(request, null, 2)}`,
    run: async () => { await createDocumentIndex(auth.api, props.targetDb, props.collection!.name, request); return 1; },
  };
}

function stageDropIndex(name: string): void {
  if (!props.collection) return;
  pending.value = { label: '删除 Document 索引', detail: name, severity: 'danger', command: `dropIndex ${props.collection.name}.${name}`,
    run: async () => { await dropDocumentIndex(auth.api, props.targetDb, props.collection!.name, name); return 1; } };
}

function stageRebuildIndex(name: string): void {
  if (!props.collection) return;
  pending.value = { label: '重建 Document 索引', detail: name, severity: 'write', command: `rebuildIndex ${props.collection.name}.${name}`,
    run: async () => { await runMaintenance(auth.api, props.targetDb, { operation: 'rebuild_index', targetModel: 'document_json', targetOwner: props.collection!.name, targetName: name }); return 1; } };
}

async function validateIndexes(): Promise<void> {
  if (!props.collection) return;
  readBusy.value = true; errorMsg.value = '';
  try { consistency.value = await validateDocumentIndexes(auth.api, props.targetDb, props.collection.name); }
  catch (error) { handleError(error, '索引一致性校验失败', 'validate_indexes'); }
  finally { readBusy.value = false; }
}

async function confirmPending(): Promise<void> {
  if (!pending.value) return;
  const operation = pending.value; const started = performance.now(); writeBusy.value = true; errorMsg.value = '';
  try {
    const affected = await operation.run();
    recordHistory('success', operation.label, 'advanced_write', operation.command, `affected ${affected}`, 0, affected, performance.now() - started);
    message.success(`${operation.label}已完成。`); pending.value = null; emit('refreshSchema'); emit('refreshDocuments');
    if (props.mode === 'indexes') await validateIndexes();
    if (props.mode === 'update') await runUpdatePreview();
  } catch (error) { handleError(error, `${operation.label}失败`, 'advanced_write', operation.command, performance.now() - started); }
  finally { writeBusy.value = false; }
}

function clearPending(): void { pending.value = null; }

async function startFeed(): Promise<void> {
  pauseFeed(); feedItems.value = []; feedResumeToken.value = ''; selectedFeedSequence.value = 0; feedActive.value = true;
  await pollFeed(); scheduleFeed();
}
function pauseFeed(): void { feedActive.value = false; if (feedTimer !== undefined) window.clearInterval(feedTimer); feedTimer = undefined; }
function scheduleFeed(): void { if (feedTimer !== undefined) window.clearInterval(feedTimer); if (feedActive.value) feedTimer = window.setInterval(() => { void pollFeed(); }, feedInterval.value); }
function clearFeed(): void { feedItems.value = []; selectedFeedSequence.value = 0; }

async function pollFeed(): Promise<void> {
  if (!props.collection || feedBusy.value) return;
  feedBusy.value = true; errorMsg.value = '';
  try {
    let hasMore = true; let rounds = 0;
    while (hasMore && rounds < 10) {
      const response = await readDocumentChangeFeed(auth.api, props.targetDb, props.collection.name, {
        resumeToken: feedResumeToken.value || null, startAt: feedStartAt.value, limit: 200,
        operations: feedOperations.value.length > 0 ? feedOperations.value : null,
        documentId: feedDocumentId.value.trim() || null,
      });
      feedResumeToken.value = response.resumeToken; resumeExpiresAt.value = response.resumeTokenExpiresAtUtc;
      latestSequence.value = response.latestSequence; hasMore = response.hasMore; rounds += 1;
      const merged = new Map(feedItems.value.map((item) => [item.sequence, item]));
      response.changes.forEach((item) => merged.set(item.sequence, item));
      feedItems.value = [...merged.values()].sort((a, b) => b.sequence - a.sequence).slice(0, 1000);
    }
    feedLastPollAt.value = new Date().toISOString();
    if (!selectedFeedSequence.value) selectedFeedSequence.value = feedItems.value[0]?.sequence ?? 0;
  } catch (error) { pauseFeed(); handleError(error, '读取 Change Feed 失败', 'change_feed'); }
  finally { feedBusy.value = false; }
}

function parsePaths(): string[] { return [...new Set(indexPathsText.value.split(/[\n,]/).map((item) => item.trim()).filter(Boolean))]; }
function indexMode(row: { paths?: string[] | null; isUnique?: boolean; isSparse?: boolean; isPartial?: boolean; isTtl?: boolean }): string {
  const values = [] as string[];
  if ((row.paths?.length ?? parsePaths().length) > 1) values.push('Compound');
  if (row.isUnique) values.push('Unique'); if (row.isSparse) values.push('Sparse');
  if ('isPartial' in row && row.isPartial) values.push('Partial'); if ('isTtl' in row && row.isTtl) values.push('TTL');
  return values.join(' · ') || 'Standard';
}
function parseJson<T>(text: string, messageText: string): { ok: true; value: T } | { ok: false; message: string } {
  try { return { ok: true, value: JSON.parse(text) as T }; } catch { return { ok: false, message: messageText }; }
}
function formatJson(value: unknown): string { return value == null ? '(none)' : JSON.stringify(value, null, 2); }
function formatTime(value: string): string { return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }); }
function operationTagType(operation: string): 'success' | 'warning' | 'error' | 'default' { return operation === 'insert' ? 'success' : operation === 'update' ? 'warning' : operation === 'delete' ? 'error' : 'default'; }
function errorToMessage(error: unknown, fallback: string): string {
  if (typeof error === 'object' && error && 'response' in error) {
    const data = (error as { response?: { data?: { message?: string } } }).response?.data;
    if (data?.message) return data.message;
  }
  return error instanceof Error ? error.message : fallback;
}
function handleError(error: unknown, fallback: string, action: string, command = '', elapsedMs = 0): void {
  const msg = errorToMessage(error, fallback); errorMsg.value = msg;
  recordHistory('error', fallback, action, command, msg, 0, 0, elapsedMs);
}
function recordHistory(status: 'success' | 'error', title: string, action: string, command: string, summary: string, rowCount: number, recordsAffected: number, elapsedMs = 0): void {
  history.record({ kind: action.includes('preview') || action === 'change_feed' ? 'query' : 'operation', status, title,
    target: props.collection?.name ?? '', database: props.targetDb, connectionId: connections.activeProfileId,
    connectionName: connections.activeProfile.name, model: 'document', action, command, summary, rowCount, recordsAffected, elapsedMs });
}

watch(() => props.collection?.name, () => { previewItems.value = []; previewRequest.value = null; consistency.value = null; pauseFeed(); clearFeed(); feedResumeToken.value = ''; });
watch(feedInterval, scheduleFeed);
watch([feedOperations, feedDocumentId, feedStartAt], () => {
  if (!feedActive.value) return;
  pauseFeed();
  feedResumeToken.value = '';
  message.info('Change Feed 过滤条件已变化，请重新开始监听。');
}, { deep: true });
onBeforeUnmount(pauseFeed);
</script>

<style scoped>
.advanced-workbench { display: flex; min-height: 520px; flex: 1; flex-direction: column; gap: 14px; padding: 16px; overflow: auto; background: #fff; }
.advanced-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 14px; padding-bottom: 12px; border-bottom: 1px solid var(--sndb-border); }
.advanced-head > div:first-child { display: flex; flex-direction: column; gap: 3px; }
.advanced-title { color: var(--sndb-ink-strong); font-size: 15px; font-weight: 800; }
.editor-pair, .diff-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; }
.field-block { display: flex; flex-direction: column; gap: 6px; }
.field-block > span, .diff-grid header { color: var(--sndb-ink-soft); font-size: 11px; font-weight: 800; text-transform: uppercase; }
.action-bar, .feed-controls, .option-strip, .conditional-row, .index-primary-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.short-control { width: 90px; }.medium-control { width: 220px; }
.diff-shell { display: grid; min-height: 300px; grid-template-columns: 190px minmax(0, 1fr); overflow: hidden; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); }
.diff-list, .feed-list { display: flex; flex-direction: column; gap: 3px; min-height: 0; overflow: auto; padding: 6px; background: var(--sndb-surface); }
.diff-list { border-right: 1px solid var(--sndb-border); }
.diff-list button, .feed-event { display: flex; border: 0; background: transparent; color: inherit; cursor: pointer; text-align: left; }
.diff-list button { flex-direction: column; gap: 2px; padding: 7px 8px; border-radius: 4px; }
.diff-list button:hover, .diff-list button.is-active, .feed-event:hover, .feed-event.is-active { background: var(--sndb-hover); }
.diff-list span { color: var(--sndb-ink-soft); font-size: 11px; }
.diff-grid { min-width: 0; padding: 10px; }
.diff-grid article { display: flex; min-width: 0; flex-direction: column; gap: 6px; }
.diff-grid pre { min-height: 220px; max-height: 420px; margin: 0; overflow: auto; padding: 10px; border: 1px solid var(--sndb-border); border-radius: 4px; background: #fbfcfe; color: #24384f; font: 12px/1.5 "Cascadia Code", Consolas, monospace; white-space: pre-wrap; word-break: break-word; }
.index-designer { display: flex; flex-direction: column; gap: 10px; padding: 12px; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); background: var(--sndb-surface); }
.index-primary-row { display: grid; grid-template-columns: minmax(140px, .7fr) minmax(240px, 1.4fr) auto; }
.option-strip label { display: inline-flex; align-items: center; gap: 5px; color: var(--sndb-ink-soft); font-size: 12px; }
.conditional-row { display: grid; grid-template-columns: minmax(180px, 1fr) 160px minmax(180px, 1fr); }
.conditional-row--ttl { grid-template-columns: minmax(180px, 1fr) 180px; }
.consistency-strip { display: flex; justify-content: space-between; gap: 12px; padding: 9px 12px; border-left: 3px solid #18a058; background: rgba(24, 160, 88, .08); color: #17633b; }
.consistency-strip.is-error { border-left-color: #d03050; background: rgba(208, 48, 80, .08); color: #8f1d35; }
.index-table-wrap { min-height: 260px; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); overflow: hidden; }
.live-dot { width: 9px; height: 9px; border-radius: 50%; background: #94a3b8; }.live-dot.is-live { background: #18a058; box-shadow: 0 0 0 4px rgba(24, 160, 88, .12); }
.feed-controls { display: grid; grid-template-columns: 150px minmax(180px, 1fr) minmax(180px, 1fr) 110px auto; }
.feed-status { display: flex; gap: 16px; padding: 7px 10px; border-top: 1px solid var(--sndb-border); border-bottom: 1px solid var(--sndb-border); color: var(--sndb-ink-soft); font-size: 11px; }
.feed-layout { display: grid; flex: 1; min-height: 390px; grid-template-columns: 330px minmax(0, 1fr); overflow: hidden; border: 1px solid var(--sndb-border); border-radius: var(--sndb-radius); }
.feed-list { border-right: 1px solid var(--sndb-border); }
.feed-event { display: grid; grid-template-columns: 52px 58px minmax(0, 1fr) auto; align-items: center; gap: 7px; min-height: 38px; padding: 5px 7px; border-left: 2px solid transparent; }
.feed-event.is-insert { border-left-color: #18a058; }.feed-event.is-update { border-left-color: #f0a020; }.feed-event.is-delete { border-left-color: #d03050; }
.feed-event__seq, .feed-event time { color: var(--sndb-ink-soft); font-size: 11px; }.feed-event strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.feed-detail { display: flex; min-width: 0; flex-direction: column; gap: 10px; padding: 10px; overflow: auto; }
.feed-detail__meta { display: flex; align-items: center; gap: 8px; color: var(--sndb-ink-soft); }
@media (max-width: 900px) { .advanced-head { flex-direction: column; }.editor-pair, .diff-grid, .diff-shell, .feed-layout, .index-primary-row, .conditional-row, .conditional-row--ttl, .feed-controls { grid-template-columns: 1fr; }.diff-list, .feed-list { max-height: 220px; border-right: 0; border-bottom: 1px solid var(--sndb-border); }.feed-status { flex-wrap: wrap; } }
</style>
